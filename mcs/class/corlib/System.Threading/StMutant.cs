//
// System.Threading.StMutant.cs
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
	internal abstract class StMutant : StWaitable
	{
		/*
		 * The boolean state of the mutant (signalled/non-signalled) is stored
		 * in the *head.next* field, as follows:
		 * - head.next == SET: the mutant is signalled and its queue is empty;
		 * - head.next == null: the mutant is non-signalled and its queue is empty;
		 * - head.next == INFLATED: the mutant is a SynchronizationEvent and is inflated;
		 * - others: the mutant isn't signalled and its queue is non-empty.
		 */

		protected static readonly StWaitBlock SET = StWaitBlock.SENTINEL;

		protected volatile StWaitBlock head;
		protected volatile StWaitBlock tail;

		/*
		 * The predecessor of a wait block that must be unlinked
		 * when the right conditions are met.
		 */

		private volatile StWaitBlock toUnlink;
		private readonly int spinCount;

		internal StMutant (bool initialState, int sc)
		{
			head = tail = new StWaitBlock ();
			if (initialState) {
				head.next = SET;
			}
			spinCount = Environment.ProcessorCount > 0 ? sc : 0;
		}

		internal override bool _AllowsAcquire {
			get { return head.next == SET; }
		}

		internal override void _UndoAcquire ()
		{
			_Release ();
		}

		internal override StWaitBlock _WaitAnyPrologue (StParker pk, int key,
		                                                ref StWaitBlock hint, ref int sc)
		{
			StWaitBlock wb = null;
			do {
				if (_TryAcquire ()) {
					return null;
				}

				if (wb == null) {
					wb = new StWaitBlock (pk, WaitType.WaitAny, 1, key);
				}

				if (EnqueueWaiter (wb, out hint)) {
					sc = hint == head ? spinCount : 0;
					return wb;
				}
				
				if (head.next == INFLATED) {
					return null;
				}
			} while (true);
		}

		internal override StWaitBlock _WaitAllPrologue (StParker pk, ref StWaitBlock hint,
		                                                ref int sc)
		{
         if (_AllowsAcquire) {
            return null;
         }
                
			var wb = new StWaitBlock (pk, WaitType.WaitAll, 1, StParkStatus.StateChange);
         
         if (EnqueueWaiter (wb, out hint)) {
            sc = hint == head ? spinCount : 0;
            return wb;
         }

         return null;
		}

		protected abstract bool EnqueueWaiter (StWaitBlock wb, out StWaitBlock pred);
		
		internal override void _CancelAcquire (StWaitBlock wb, StWaitBlock hint)
		{
			while (hint.next == wb) {

				/*
				 * Remove the cancelled wait blocks that are at the front
				 * of the queue.
				 */

				StWaitBlock h;
				StWaitBlock hn = (h = head).next;

				if (hn == INFLATED) {
					return;
				}

				if (hn != null && hn != SET && hn.parker.IsLocked) {
					TryAdvanceHead (h, hn);
					continue;
				}

				/*
				 * If the queue is empty, return.
				 */

				StWaitBlock t, tn;
				if ((t = tail) == h) {
					return;
				}

				/*
				 * Do the necessary consistency checks before trying to
				 * unlink the wait block.
				 */

				if ((tn = t.next) != null) {
					AdvanceTail (t, tn);
					continue;
				}

				/*
				 * If the wait block is not at the tail of the queue, try
				 * to unlink it.
				 */

				if (wb != t) {
					StWaitBlock wbn;
					if ((wbn = wb.next) == wb || hint.CasNext (wb, wbn)) {
						return;
					}
				}

				/*
				 * The wait block is at the tail of the queue; so, take
				 * into account the *toUnlink* wait block.
				 */

				StWaitBlock dp;
				if ((dp = toUnlink) != null) {
					StWaitBlock d, dn;
					if ((d = dp.next) == dp ||
					    ((dn = d.next) != null && dp.CasNext (d, dn))) {
						CasToUnlink (dp, null);
					}
					if (dp == hint) {
						return; // *wb* is an already the saved node.
					}
				} else if (CasToUnlink (null, hint)) {
					return;
				}
			}
		}

		protected void AdvanceTail (StWaitBlock t, StWaitBlock nt)
		{
			if (t == tail) {
				Interlocked.CompareExchange (ref tail, nt, t);
			}
		}

		protected bool TryAdvanceHead (StWaitBlock h, StWaitBlock nh)
		{
			if (h == head && Interlocked.CompareExchange (ref head, nh, h) == h) {
				h.next = h; // Mark the old head as unlinked.
				return true;
			}
			return false;
		}

		private bool CasToUnlink (StWaitBlock tu, StWaitBlock ntu)
		{
			return toUnlink == tu &&
			       Interlocked.CompareExchange (ref toUnlink, ntu, tu) == tu;
		}
	}
}