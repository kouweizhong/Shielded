using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using System.Linq;

namespace Shielded
{
    /// <summary>
    /// Central class of the Shielded library. Contains, among other things, methods
    /// for directly running transactions, and creating conditional transactions.
    /// </summary>
    public static class Shield
    {
        [ThreadStatic]
        private static TransactionContextInternal _context;

        internal static TransactionContext Context
        {
            get
            {
                return _context;
            }
        }

        /// <summary>
        /// Current transaction's read stamp, i.e. the latest version it can read.
        /// Thread-static. Throws if called out of transaction.
        /// </summary>
        public static long ReadStamp
        {
            get
            {
                AssertInTransaction();
                return _context.ReadTicket.Stamp;
            }
        }

        /// <summary>
        /// Gets a value indicating whether the current thread is in a transaction.
        /// </summary>
        public static bool IsInTransaction
        {
            get
            {
                return _context != null;
            }
        }

        /// <summary>
        /// Throws <see cref="InvalidOperationException"/> if called out of a transaction.
        /// </summary>
        public static void AssertInTransaction()
        {
            if (_context == null)
                throw new InvalidOperationException("Operation needs to be in a transaction.");
        }



        [ThreadStatic]
        private static ICommutableShielded _blockEnlist;
        [ThreadStatic]
        private static bool _enforceTracking;
        [ThreadStatic]
        private static int? _commuteTime;

        /// <summary>
        /// Enlist the specified item in the transaction. Returns true if this is the
        /// first time in this transaction that this item is enlisted. hasLocals indicates
        /// if this item already has local storage prepared. If true, it means it must have
        /// enlisted already. However, in IsolatedRun you may have locals, even though you
        /// have not yet enlisted in the isolated items! This param dictates the response
        /// if it is set to true, adding to the isolated items is not revealed by the retval.
        /// </summary>
        internal static bool Enlist(IShielded item, bool hasLocals, bool write)
        {
            AssertInTransaction();
            var ctx = _context;
            if (write && ctx.CommitCheckDone && !item.HasChanges)
                throw new InvalidOperationException("New writes not allowed here.");
            if (_blockEnlist != null && _blockEnlist != item)
                throw new InvalidOperationException("Accessing other shielded fields in this context is forbidden.");

            var items = ctx.Items;
            if (hasLocals)
            {
                if (_enforceTracking)
                    items.Enlisted.Add(item);
                items.HasChanges = items.HasChanges || write;
                return false;
            }

            items.HasChanges = items.HasChanges || write;
            if (!items.Enlisted.Contains(item))
            {
                if (ctx.CommitCheckDone)
                    throw new InvalidOperationException("Cannot access new fields here.");
                // must not add into Enlisted before we run commutes, otherwise commutes' calls
                // to Enlist would return false, and the commutes, although running first, would
                // not actually check the lock! this also means that CheckCommutes must tolerate
                // being reentered with the same item.
                CheckCommutes(item);
                return items.Enlisted.Add(item);
            }
            return false;
        }

        /// <summary>
        /// When an item is enlisted for which we have defined commutes, it causes those
        /// commutes to be executed immediately. This happens before the field is read or
        /// written into. The algorithm here allows for one commute to trigger others,
        /// guaranteeing that they will all execute in correct order provided that each
        /// commute correctly defined the fields which, if accessed, must cause it to
        /// degenerate.
        /// </summary>
        private static void CheckCommutes(IShielded item)
        {
            var commutes = _context.Items.Commutes;
            if (commutes == null || commutes.Count == 0)
                return;

            // in case one commute triggers others, we mark where we are in _comuteTime,
            // and no recursive call will execute commutes beyond that point.
            // so, clean "dependency resolution" - we trigger only those before us. those 
            // after us just get marked, and then we execute them (or, someone lower in the stack).
            var oldTime = _commuteTime;
            var oldBlock = _blockCommute;
            int execLimit = oldTime ?? commutes.Count;
            try
            {
                if (!oldTime.HasValue)
                    _blockCommute = true;
                for (int i = 0; i < commutes.Count; i++)
                {
                    var comm = commutes[i];
                    if (comm.State == CommuteState.Ok && comm.Affecting.Contains(item))
                        comm.State = CommuteState.Broken;
                    if (comm.State == CommuteState.Broken && i < execLimit)
                    {
                        _commuteTime = i;
                        comm.State = CommuteState.Executed;
                        comm.Perform();
                    }
                }
            }
            catch
            {
                // not sure if this matters, but i like it. please note that this and the Remove in finally
                // do not necessarily affect the same commutes.
                commutes.RemoveAll(c => c.Affecting.Contains(item));
                throw;
            }
            finally
            {
                _commuteTime = oldTime;
                if (!oldTime.HasValue)
                {
                    _blockCommute = oldBlock;
                    commutes.RemoveAll(c => c.State != CommuteState.Ok);
                }
            }
        }

        [ThreadStatic]
        private static bool _blockCommute;

        /// <summary>
        /// The strict version of EnlistCommute, which will monitor that the code in
        /// perform does not enlist anything except the one item, affecting.
        /// </summary>
        internal static void EnlistStrictCommute(Action perform, ICommutableShielded affecting)
        {
            EnlistCommute(() => {
                // if a strict commute is enlisted from within another strict commute...
                // if != null, this definitely equals our affecting. even if not so,
                // perform trying to access anything else will be prevented in Enlist().
                if (_blockEnlist != null)
                {
                    perform();
                    return;
                }

                try
                {
                    _blockEnlist = affecting;
                    perform();
                }
                finally
                {
                    _blockEnlist = null;
                }
            }, affecting);
        }

        /// <summary>
        /// The action is performed just before commit, and reads the latest
        /// data. If it conflicts, only it is retried. If it succeeds,
        /// we (try to) commit with the same write stamp along with it.
        /// The affecting param determines the IShieldeds that this transaction must
        /// not access, otherwise this commute must degenerate - it gets executed
        /// now, or at the moment when any of these IShieldeds enlists.
        /// </summary>
        internal static void EnlistCommute(Action perform, params ICommutableShielded[] affecting)
        {
            if (affecting == null)
                throw new ArgumentException();
            if (_blockEnlist != null &&
                (affecting.Length != 1 || affecting[0] != _blockEnlist))
                throw new InvalidOperationException("No shielded field access is allowed in this context.");
            AssertInTransaction();

            var items = _context.Items;
            if (_blockCommute || items.Enlisted.Overlaps(affecting))
                perform(); // immediate degeneration. should be some warning.
            else
            {
                items.HasChanges = true;
                if (items.Commutes == null)
                    items.Commutes = new List<Commute>();
                items.Commutes.Add(new Commute()
                {
                    Perform = perform,
                    Affecting = affecting
                });
            }
        }

        /// <summary>
        /// Conditional transaction, which executes after some fields are committed into.
        /// Does not execute immediately! Test is executed once just to get a read pattern,
        /// result is ignored. It will later be re-executed when any of the accessed
        /// IShieldeds commits.
        /// When test passes, executes trans. Test is executed in a normal transaction. If it
        /// changes access patterns between calls, the subscription changes as well!
        /// Test and trans are executed in single transaction, and if the commit fails, test
        /// is also retried!
        /// </summary>
        /// <returns>An IDisposable which can be used to cancel the conditional by calling
        /// Dispose on it. Dispose can be called from trans.</returns>
        public static IDisposable Conditional(Func<bool> test, Action trans)
        {
            if (test == null || trans == null)
                throw new ArgumentNullException();
            return new CommitSubscription(CommitSubscriptionContext.PostCommit, test, trans);
        }

        /// <summary>
        /// Pre-commit check, which executes just before commit of a transaction involving
        /// certain fields. Can be used to ensure certain invariants hold, for example.
        /// Does not execute immediately! Test is executed once just to get a read pattern,
        /// result is ignored. It will later be re-executed just before commit of any transaction
        /// that changes one of the fields it accessed.
        /// If test passes, executes trans as well. They will execute within the transaction
        /// that triggers them. If they access a commuted field, the commute will degenerate.
        /// </summary>
        /// <returns>An IDisposable which can be used to cancel the pre-commit by calling
        /// Dispose on it. Dispose can be called from trans.</returns>
        public static IDisposable PreCommit(Func<bool> test, Action trans)
        {
            if (test == null || trans == null)
                throw new ArgumentNullException();
            return new CommitSubscription(CommitSubscriptionContext.PreCommit, test, trans);
        }

        /// <summary>
        /// Execute an action during every commit which changes fields of a type.
        /// The action will be immediately enlisted, and get called after any commit is
        /// checked, but before anything is written. Any exceptions bubble out to the thread
        /// running the transaction (or, on calling <see cref="CommitContinuation.Commit"/>).
        /// Calls to <see cref="Rollback"/> are not allowed.
        /// The action may not access any fields that the transaction did not already access,
        /// and it may only write into fields which were already written to by the transaction.
        /// This method throws when called within a transaction.
        /// </summary>
        /// <returns>An IDisposable for unsubscribing.</returns>
        public static IDisposable WhenCommitting<T>(Action<IEnumerable<T>> act) where T : class
        {
            if (act == null)
                throw new ArgumentNullException();
            if (IsInTransaction)
                throw new InvalidOperationException("Operation not allowed in transaction.");
            return new CommittingSubscription(
                fields => {
                    List<T> interesting = null;
                    for (int i = 0; i < fields.Length; i++)
                    {
                        var field = fields[i];
                        T obj;
                        if (field.HasChanges && (obj = field.Field as T) != null)
                        {
                            if (interesting == null)
                                interesting = new List<T>();
                            interesting.Add(obj);
                        }
                    }
                    if (interesting != null)
                        act(interesting);
                });
        }

        /// <summary>
        /// Execute an action during every writing commit which reads or changes a given field.
        /// The lambda receives a bool indicating whether the field has changes.
        /// The action will be immediately enlisted, and get called after any commit is
        /// checked, but before anything is written. Any exceptions bubble out to the thread
        /// running the transaction (or, on calling <see cref="CommitContinuation.Commit"/>).
        /// Calls to <see cref="Rollback"/> are not allowed.
        /// The action may not access any fields that the transaction did not already access,
        /// and it may only write into fields which were already written to by the transaction.
        /// This method throws when called within a transaction.
        /// </summary>
        /// <returns>An IDisposable for unsubscribing.</returns>
        public static IDisposable WhenCommitting(object field, Action<bool> act)
        {
            if (field == null || act == null)
                throw new ArgumentNullException();
            return WhenCommitting(fields => {
                var tf = fields.FirstOrDefault(f => f.Field == field);
                if (tf.Field != null)
                    act(tf.HasChanges);
            });
        }

        /// <summary>
        /// Execute an action during every writing commit.
        /// The action will be immediately enlisted, and get called after any commit is
        /// checked, but before anything is written. Any exceptions bubble out to the thread
        /// running the transaction (or, on calling <see cref="CommitContinuation.Commit"/>).
        /// Calls to <see cref="Rollback"/> are not allowed.
        /// The action may not access any fields that the transaction did not already access,
        /// and it may only write into fields which were already written to by the transaction.
        /// This version receives a full list of enlisted items, even those without changes.
        /// This method throws when called within a transaction.
        /// </summary>
        /// <returns>An IDisposable for unsubscribing.</returns>
        public static IDisposable WhenCommitting(Action<IEnumerable<TransactionField>> act)
        {
            if (act == null)
                throw new ArgumentNullException();
            if (IsInTransaction)
                throw new InvalidOperationException("Operation not allowed in transaction.");
            return new CommittingSubscription(act);
        }

        /// <summary>
        /// Enlists a side-effect - an operation to be performed only if the transaction
        /// commits. Optionally receives an action to perform in case of a rollback.
        /// If the transaction is rolled back, all enlisted side-effects are (also) cleared.
        /// Should be used to perform all IO and such operations (except maybe logging).
        /// 
        /// If this is called out of transaction, the fx action (if one was provided)
        /// will be directly executed. This preserves correct behavior if the call finds
        /// itself sometimes in, sometimes out of transaction, because of some crazy
        /// nesting differences.
        /// </summary>
        public static void SideEffect(Action fx, Action rollbackFx = null)
        {
            if (fx == null && rollbackFx == null)
                throw new ArgumentNullException(null, "At least one arg must be != null.");
            if (!IsInTransaction)
            {
                if (fx != null) fx();
                return;
            }

            if (_context.Items.Fx == null)
                _context.Items.Fx = new List<SideEffect>();
            _context.Items.Fx.Add(new SideEffect(fx, rollbackFx));
        }

        /// <summary>
        /// Executes the function in a transaction, and returns its final result.
        /// Transactions may, in case of conflicts, get repeated from beginning. Your
        /// delegate should be ready for this. If you wish to do IO or similar
        /// operations, which should not be repeated, pass them to
        /// <see cref="Shield.SideEffect"/>. Nesting InTransaction calls is allowed,
        /// the nested transactions are treated as normal parts of the outer transaction.
        /// </summary>
        public static T InTransaction<T>(Func<T> act)
        {
            T retVal = default(T);
            Shield.InTransaction(() => { retVal = act(); });
            return retVal;
        }

        /// <summary>
        /// Executes the action in a transaction.
        /// Transactions may, in case of conflicts, get repeated from beginning. Your
        /// delegate should be ready for this. If you wish to do IO or similar
        /// operations, which should not be repeated, pass them to
        /// <see cref="Shield.SideEffect"/>. Nesting InTransaction calls is allowed,
        /// the nested transactions are treated as normal parts of the outer transaction.
        /// </summary>
        public static void InTransaction(Action act)
        {
            if (IsInTransaction)
                act();
            else
                TransactionLoop(act, () => { _context.DoCommit(); });
        }

        /// <summary>
        /// Runs a transaction, with repetitions if needed, until it passes the commit check,
        /// and then stops. The fields that will be written into, remain locked! It returns
        /// an object which can be used to later commit the transaction, roll it back, or run
        /// code inside of its scope, restricted to fields touched by the original transaction.
        /// Receives a timeout in milliseconds, and the returned continuation will, if not
        /// completed by then, automatically roll back by this time.
        /// </summary>
        public static CommitContinuation RunToCommit(int timeoutMs, Action act)
        {
            if (IsInTransaction)
                throw new InvalidOperationException("Operation not allowed in transaction.");
            CommitContinuation res = null;
            try
            {
                TransactionLoop(act, () => {
                    res = _context;
                    _context = null;
                });
                return res;
            }
            finally
            {
                if (res != null && !res.Completed)
                    res.StartTimer(timeoutMs);
            }
        }

        static void TransactionLoop(Action act, Action onChecked)
        {
            while (true)
            {
                try
                {
                    _context = new TransactionContextInternal();
                    act();
                    if (CommitCheck())
                    {
                        onChecked();
                        return;
                    }
                    _context.DoCheckFailed();
                }
                catch (TransException) { }
                finally
                {
                    if (_context != null)
                        _context.DoRollback();
                }
            }
        }

        /// <summary>
        /// Rolls the transaction back and retries it from the beginning. If you don't
        /// want the transaction to repeat, throw and catch an exception yourself.
        /// </summary>
        public static void Rollback()
        {
            AssertInTransaction();
            if (_context.CommitCheckDone)
                throw new InvalidOperationException("Rollback not allowed for checked transactions.");
            throw new TransException("Requested rollback and retry.");
        }

        /// <summary>
        /// Runs the action, and returns a set of IShieldeds that the action enlisted.
        /// It will make sure to restore original enlisted items, merged with the ones
        /// that the action enlisted, before returning. This is important to make sure
        /// all thread-local state is cleared on transaction exit. The isolated action
        /// may still cause outer transaction's commutes to degenerate.
        /// </summary>
        internal static ISet<IShielded> IsolatedRun(Action act)
        {
            var isolated = new TransItems();
            isolated.Commutes = _context.Items.Commutes;
            WithTransactionContext(isolated, act);
            return isolated.Enlisted;
        }

        #region Commit & rollback

        /// <summary>
        /// Create a local transaction context by replacing local context's item container with
        /// <paramref name="isolatedItems"/> and setting <see cref="Shield._blockCommute"/> to <c>true</c>,
        /// then perform <paramref name="act"/> and return with restored state. Forces all IShieldeds to
        /// enlist, regardless of previous enlist status.
        /// </summary>
        /// <param name="isolatedItems">The instance which temporarily replaces local context's item container.</param>
        /// <param name="act">The action to execute in the context.</param>
        /// <param name="merge">Whether to merge the isolated items into the original items when done. Defaults to <c>true</c>.
        /// If items are left unmerged, take care to handle TransExceptions and roll back the items yourself!</param>
        private static void WithTransactionContext(
            TransItems isolatedItems, Action act, bool merge = true)
        {
            var originalItems = _context.Items;
            var oldEnforce = _enforceTracking;
            var oldBlock = _blockCommute;
            try
            {
                Shield._context.Items = isolatedItems;
                Shield._blockCommute = true;
                Shield._enforceTracking = true;

                act();
            }
            finally
            {
                if (merge) originalItems.UnionWith(isolatedItems);
                Shield._context.Items = originalItems;
                Shield._blockCommute = oldBlock;
                Shield._enforceTracking = oldEnforce;
            }
        }

        /// <summary>
        /// Executes the commutes, returning through the out param the set of items that the
        /// commutes had accessed.
        /// Increases the current start stamp, and leaves the commuted items unmerged with the
        /// main transaction items!
        /// </summary>
        static void RunCommutes(out TransItems commutedItems)
        {
            var commutes = _context.Items.Commutes;
            Action commuteRunner = () => {
                for (int i = 0; i < commutes.Count; i++)
                    commutes[i].Perform();
            };

            while (true)
            {
                _context.ReadTicket = VersionList.GetUntrackedReadStamp();
                commutedItems = new TransItems();
                try
                {
                    WithTransactionContext(commutedItems, commuteRunner, merge: false);
                    return;
                }
                catch (TransException)
                {
#if USE_STD_HASHSET
                    foreach (var item in commutedItems.Enlisted)
                        item.Rollback();
#else
                    commutedItems.Enlisted.Rollback();
#endif
                    commutedItems = null;
                }
            }
        }

        private static bool HasChanges(IShielded item)
        {
            return item.HasChanges;
        }

        private static readonly object _checkLock = new object();

        /// <summary>
        /// Performs the commit check, returning the outcome. Prepares the write ticket in the context.
        /// The ticket is obtained before leaving the lock here, so that any thread which
        /// later conflicts with us will surely (in its retry) read the version we are now
        /// writing. The idea is to avoid senseless repetitions, retries after which a
        /// thread would again read old data and be doomed to fail again.
        /// It is critical that this ticket be marked when complete (i.e. Changes set to
        /// something non-null), because trimming will not go past it until this happens.
        /// </summary>
        static bool CommitCheck()
        {
            var ctx = _context;
            var items = ctx.Items;
            if (!items.HasChanges)
                return true;

            TransItems commutedItems = null;
            var oldReadTicket = ctx.ReadTicket;
            bool commit = false;
            bool brokeInCommutes = items.Commutes != null && items.Commutes.Count > 0;

            if (CommitSubscriptionContext.PreCommit.Count > 0)
            {
                // if any commute would trigger a pre-commit check, this check could, if executed
                // in the commute sub-transaction, see newer values in fields which
                // were read (but not written to) by the main transaction. commutes are normally
                // very isolated to prevent this, but pre-commits we cannot isolate.
                // so, commutes trigger them now, and they cause the commutes to degenerate.
                CommitSubscriptionContext.PreCommit
                    .Trigger(brokeInCommutes ?
                        items.Enlisted.Where(HasChanges).Concat(
                            items.Commutes.SelectMany(c => c.Affecting)) :
                        items.Enlisted.Where(HasChanges))
                    .Run();
            }

            try
            {
repeatCommutes: if (brokeInCommutes)
                {
                    RunCommutes(out commutedItems);
#if DEBUG
                    if (items.Enlisted.Overlaps(commutedItems.Enlisted))
                        throw new InvalidOperationException("Invalid commute - conflict with transaction.");
#endif
                }

                var writeStamp = ctx.WriteStamp = new WriteStamp(ctx);
                lock (_checkLock)
                {
                    try
                    {
                        if (brokeInCommutes)
#if USE_STD_HASHSET
                            foreach (var item in commutedItems.Enlisted)
                                if (!item.CanCommit(writeStamp))
                                    goto repeatCommutes;
#else
                            if (!commutedItems.Enlisted.CanCommit(writeStamp))
                                goto repeatCommutes;
#endif

                        ctx.ReadTicket = oldReadTicket;
                        brokeInCommutes = false;
#if USE_STD_HASHSET
                        foreach (var item in items.Enlisted)
                            if (!item.CanCommit(writeStamp))
                                return false;
#else
                        if (!items.Enlisted.CanCommit(writeStamp))
                            return false;
#endif

                        commit = true;
                    }
                    finally
                    {
                        if (!commit)
                        {
#if USE_STD_HASHSET
                            if (commutedItems != null)
                                foreach (var item in commutedItems.Enlisted)
                                    item.Rollback();
                            if (!brokeInCommutes)
                                foreach (var item in items.Enlisted)
                                    item.Rollback();
#else
                            if (commutedItems != null)
                                commutedItems.Enlisted.Rollback();
                            if (!brokeInCommutes)
                                items.Enlisted.Rollback();
#endif
                        }
                        else
                            VersionList.NewVersion(writeStamp, out ctx.WriteTicket);
                    }
                }
                return true;
            }
            finally
            {
                ctx.ReadTicket = oldReadTicket;
                ctx.CommitCheckDone = true;
                // note that this changes the _localItems.Enlisted hashset to contain the
                // commute-enlists as well, regardless of the check outcome.
                if (commutedItems != null)
                    items.UnionWith(commutedItems);
            }
        }

        private class TransactionContextInternal : TransactionContext
        {
            public ReadTicket ReadTicket;
            public WriteTicket WriteTicket;
            public WriteStamp WriteStamp;
            public TransItems Items;
            public bool CommitCheckDone;

            public TransactionContextInternal()
            {
                Items = new TransItems();
                VersionList.GetReaderTicket(out ReadTicket);
            }

            public override TransactionField[] Fields
            {
                get
                {
                    return _fields ?? (_fields = Items.GetFields());
                }
            }
            private TransactionField[] _fields;

            public override void InContext(Action act)
            {
                if (Shield._context != null)
                    throw new InvalidOperationException("Operation not allowed in a transaction.");
                if (Completed)
                    throw new InvalidOperationException("Transaction is completed.");
                try
                {
                    Shield._context = this;
                    act();
                }
                finally
                {
                    Shield._context = null;
                }
            }

            private void Complete(bool committed)
            {
                try { }
                finally
                {
                    if (WriteTicket != null && WriteTicket.Changes == null)
                        WriteTicket.Changes = Enumerable.Empty<IShielded>();
                    if (ReadTicket != null)
                        VersionList.ReleaseReaderTicket(ref ReadTicket);
                    if (WriteStamp != null && WriteStamp.Locked)
                        WriteStamp.Release();
                    Committed = committed;
                    Completed = true;
                    Shield._context = null;
                }
            }

            private int _committing;
            private int _completing;

            private bool Sync(ref int flag, Action act)
            {
                return Sync(ref flag, () => { act(); return true; });
            }

            // returns true iff it wins the flag, and the lambda returns true.
            private bool Sync(ref int flag, Func<bool> act)
            {
                var effect = false;
                try
                {
                    try { } finally
                    {
                        effect = Interlocked.CompareExchange(ref flag, 1, 0) == 0;
                    }
                    if (!effect || Completed)
                        return false;
                    return act();
                }
                finally
                {
                    if (effect)
                        Interlocked.Exchange(ref flag, 0);
                }
            }

            public override void Commit()
            {
                if (!TryCommit())
                    throw new InvalidOperationException("Continuation already completing or completed.");
            }

            public override bool TryCommit()
            {
                if (Shield._context != null)
                    throw new InvalidOperationException("Operation not allowed in a transaction.");
                return Sync(ref _committing, () => {
                    using (this) try
                    {
                        Shield._context = this;
                        if (Items.HasChanges)
                        {
                            CommittingSubscription.Fire(Items);
                            if (!Sync(ref _completing, CommitWChanges))
                                return false;
                        }
                        else if (!Sync(ref _completing, CommitWoChanges))
                            return false;
                    }
                    finally
                    {
                        if (Shield._context != null)
                        {
                            Sync(ref _completing, DoRollback);
                            Shield._context = null;
                        }
                    }
                    VersionList.TrimCopies();
                    return true;
                });
            }

            public void DoCommit()
            {
                if (Items.HasChanges)
                {
                    CommittingSubscription.Fire(Items);
                    CommitWChanges();
                }
                else
                    CommitWoChanges();
                VersionList.TrimCopies();
            }

            private void CommitWoChanges()
            {
#if USE_STD_HASHSET
                foreach (var item in Items.Enlisted)
                    item.Commit();
#else
                Items.Enlisted.CommitWoChanges();
#endif
                Complete(true);
                if (Items.Fx != null)
                    Items.Fx.Select(f => f.OnCommit).SafeRun();
            }

            private void CommitWChanges()
            {
                List<IShielded> trigger;
                // this must not be interrupted by a Thread.Abort!
                try { }
                finally
                {
#if USE_STD_HASHSET
                    trigger = new List<IShielded>();
                    foreach (var item in Items.Enlisted)
                    {
                        if (item.HasChanges) trigger.Add(item);
                        item.Commit();
                    }
#else
                    trigger = Items.Enlisted.Commit();
#endif
                    WriteTicket.Changes = trigger;
                }

                Complete(true);
                (Items.Fx != null ? Items.Fx.Select(f => f.OnCommit) : null)
                    .SafeConcat(CommitSubscriptionContext.PostCommit.Trigger(trigger))
                    .SafeRun();
            }

            public override void Rollback()
            {
                if (!TryRollback())
                    throw new InvalidOperationException("Continuation already completing or completed.");
            }

            public override bool TryRollback()
            {
                if (Shield._context != null)
                    throw new InvalidOperationException("Operation not allowed in a transaction.");
                return Sync(ref _completing, () => {
                    using (this) try
                    {
                        Shield._context = this;
                    }
                    finally
                    {
                        DoRollback();
                    }
                });
            }

            public void DoRollback()
            {
#if USE_STD_HASHSET
                foreach (var item in Items.Enlisted)
                    item.Rollback();
#else
                Items.Enlisted.Rollback();
#endif
                DoCheckFailed();
            }

            public void DoCheckFailed()
            {
                Complete(false);
                if (Items.Fx != null)
                    Items.Fx.Select(f => f.OnRollback).SafeRun();
            }
        }

        private static IEnumerable<T> SafeConcat<T>(this IEnumerable<T> first, IEnumerable<T> second)
        {
            if (first != null && second != null)
                return first.Concat(second);
            else if (first != null)
                return first;
            else
                return second;
        }

        #endregion
    }
}

