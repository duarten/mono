//
// System.Threading.StReentrantFairLock.cs
//
// Copyright 2011 Carlos Martins, Duarte Nunes
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
// http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//
// Author: Duarte Nunes (duarte.m.nunes@gmail.com)
//

#pragma warning disable 0420

namespace System.Threading
{
    internal sealed class StReentrantFairLock : StWaitable
    {
        private readonly StFairLock flock;

        private const int UNOWNED = 0;

        //
        // The *owner* variable does not need to be volatile.
        //

        private int owner;
        private int count;

        public StReentrantFairLock (int spinCount)
        {
            flock = new StFairLock (spinCount);
        }

        internal StReentrantFairLock ()
        {
            flock = new StFairLock ();
        }

        internal StReentrantFairLock (bool initiallyOwned)
        {
            flock = new StFairLock (initiallyOwned);
            if (initiallyOwned) {
                owner = Thread.CurrentThreadId;
            }
        }

        internal bool TryEnter ()
        {
            int tid = Thread.CurrentThreadId;

            if (owner == tid) {
                count++;
                return true;
            }

            if (flock.TryEnter ()) {
                owner = tid;
                return true;
            }

            return false;
        }

        internal bool Enter (StCancelArgs cargs)
        {
            int tid = Thread.CurrentThreadId;

            if (owner == tid) {
                count++;
                return true;
            }

            if (flock.Enter (cargs)) {
                owner = tid;
                return true;
            }

            return false;
        }

        internal void Enter ()
        {
            Enter (StCancelArgs.None);
        }

        internal void Exit ()
        {
            if (Thread.CurrentThreadId != owner) {
                throw new InvalidOperationException ();
            }

            if (count != 0) {
                count--;
                return;
            }

            owner = UNOWNED;
            flock.Exit ();
        }

        internal override bool _AllowsAcquire
        {
            get { return flock._AllowsAcquire || owner == Thread.CurrentThreadId; }
        }

        internal override bool _TryAcquire ()
        {
            return TryEnter ();
        }

        internal override bool _Release ()
        {
            if (owner != Thread.CurrentThreadId) {
                return false;
            }

            Exit ();
            return true;
        }

        internal override StWaitBlock _WaitAnyPrologue (StParker pk, int key,
                                                        ref StWaitBlock hint, ref int sc)
        {
            if (TryEnter ()) {
                if (pk.TryLock ()) {
                    pk.UnparkSelf (key);
                }
                else {
                    Exit ();
                }
                return null;
            }

            //
            // The lock is busy, so execute the WaitAny prologue on the
            // associated non-reentrant lock.
            //

            return flock._WaitAnyPrologue (pk, key, ref hint, ref sc);
        }

        internal override StWaitBlock _WaitAllPrologue (StParker pk, ref StWaitBlock hint,
                                                        ref int sc)
        {
            if (_AllowsAcquire) {
                if (pk.TryLock ()) {
                    pk.UnparkSelf (StParkStatus.StateChange);
                }

                //
                // Return null to signal that no wait block was inserted
                // in the lock's queue.
                //

                return null;
            }

            return flock._WaitAllPrologue (pk, ref hint, ref sc);
        }

        internal override void _WaitEpilogue ()
        {
            owner = Thread.CurrentThreadId;
        }

        internal override void _UndoAcquire ()
        {
            Exit ();
        }

        internal override void _CancelAcquire (StWaitBlock wb, StWaitBlock hint)
        {
            flock._CancelAcquire (wb, hint);
        }
    }
}