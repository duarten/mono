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

using System.Runtime.CompilerServices;

#pragma warning disable 0420

namespace System.Threading
{
	internal sealed class StMutex : StMutant
	{
		private const int UNOWNED = 0;
		private int count;
		internal StMutex next;
		internal StMutex prev;
		
		internal StMutex ()
			: base (true, 0) { }

		internal StMutex (int spinCount)
			: base (true, spinCount) { }


		internal StMutex (bool initiallyOwned)
			: this (!initiallyOwned, 0) { }


		internal StMutex (bool initiallyOwned, int spinCount)
			: base (!initiallyOwned, spinCount)
		{
			if (initiallyOwned) {
				head.request = Thread.CurrentThreadId;
			}
		}

		internal bool IsOwner {
			get { return head.request == Thread.CurrentThreadId; }
		}
		
		internal override bool _AllowsAcquire {
			get { return base._AllowsAcquire || IsOwner; }
		}

		internal void Exit () 
		{
			if (!_Release ()) {
				throw _SignalException;
			}
		}

		/*
		 * This is called by some arbitrary thread to release the mutex.
		 */
 
		internal StMutex Abandon ()
		{
			count = -1;
			head.request = 0;

			if (prev != null) {
				prev.next = next;
			}

			if (next != null) {
				next.prev = prev;
			}

			var ret = next;
			prev = next = null;

			ReleaseWorker (false);

			return ret;
		}

      internal override bool _TryAcquire ()
      {
			int tid = Thread.CurrentThreadId;

			if (head.request == tid) {
				++count;
				return true;
			}

      	bool acquired = false;
      	RuntimeHelpers.PrepareConstrainedRegions ();
			try { }
			finally {
				if (head.next == SET && 
					 (acquired = Interlocked.CompareExchange (ref head.next, null, SET) == SET)) {
					head.request = tid;
				}

				_WaitEpilogue ();
			}
         
			return acquired;
      }
		
      internal override bool _Release ()
      {
      	if (head.request != Thread.CurrentThreadId) {
      		return false;
      	}

      	if (count > 0) {
      		count -= 1;
      	} else {
      		ReleaseWorker (true);
      	}
      	return true;
      }

		private void ReleaseWorker (bool release)
		{
			RuntimeHelpers.PrepareConstrainedRegions ();
			try { } 
			finally {
				head.request = UNOWNED;

				if (release) {
					Thread.CurrentThread.Release (this);
				}

				do {
					StWaitBlock h, hn;
					if ((hn = (h = head).next) == SET) {
						break;
					}

					/*
					 * If the mutant's queue is empty, try to set the *head.next* field to SET.
					 */

					if (hn == null) {
						if (Interlocked.CompareExchange (ref head.next, SET, null) == null) {
							break;
						}

						continue;
					}

					if (TryAdvanceHead (h, hn)) {
						StParker pk;
						if ((pk = hn.parker).TryLock ()) {
							pk.Unpark (hn.waitKey);

							/*
							 * If this is a wait-any wait block, we are done;
							 * otherwise, keep trying to release other waiters.
							 */

							if (hn.waitType == WaitType.WaitAny) {
								break;
							}
						}
					}
				} while (true);
			}
		}

		internal override void _UndoAcquire ()
		{
			_Release ();
		}
		
		internal override Exception _SignalException {
			get { return new ApplicationException ("The calling thread does not own the mutex."); }
		}

		internal override void _WaitEpilogue ()
		{
			Thread.CurrentThread.Register (this);

			if (count == -1) {
				count = 0;
				throw new AbandonedMutexException ("Mutex has been abandoned by another thread.");
			}
		}

		protected override bool EnqueueWaiter (StWaitBlock wb, out StWaitBlock pred)
		{
			wb.request = Thread.CurrentThreadId;

			do {
				StWaitBlock t, tn;
				if ((tn = (t = tail).next) == SET) {
					pred = null;
					return false;
				}

				if (tn != null) {
					AdvanceTail (t, tn);
					continue;
				}

				if (Interlocked.CompareExchange (ref t.next, wb, null) == null) {
					AdvanceTail (t, wb);
					pred = t;
					return true;
				}
			} while (true);
		}
	}
}