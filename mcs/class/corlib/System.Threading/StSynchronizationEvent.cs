//
// System.Threading.StSynchronizationEvent.cs
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
	internal sealed class StSynchronizationEvent : StMutant
	{
		internal StSynchronizationEvent (bool initialState, int spinCount)
			: base (initialState, spinCount) { }

		internal StSynchronizationEvent (bool initialState)
			: base (initialState, 0) { }

		internal StSynchronizationEvent ()
			: base (false, 0) { }

		internal bool Set ()
		{
			return _Release ();
		}
		
		internal bool Reset ()
		{
			if (_TryAcquire()) {
				return true;
			}

			if (head.next == INFLATED) {
				bool release = false;
				try {
					swhandle.DangerousAddRef (ref release);
					return StInternalMethods.ResetEvent (swhandle.DangerousGetHandle ());
				} finally {
					if (release) {
						swhandle.DangerousRelease ();
					}
				}
			}

			return true;
		}

		internal override bool _TryAcquire ()
		{
			return head.next == SET && Interlocked.CompareExchange (ref head.next, null, SET) == SET;
		}

		internal override bool _Release ()
		{
			bool done = false;
			do {
				StWaitBlock h, hn;
				if ((hn = (h = head).next) == SET) {
					return true;
				}

				/*
				 * If the mutant's queue is empty, try to set the *head.next* field to SET.
				 */

				if (hn == null) {
					if (Interlocked.CompareExchange (ref head.next, SET, null) == null) {
						return true;
					}

					continue;
				}

				if (hn == INFLATED) {
					bool release = false;
					try {
						swhandle.DangerousAddRef (ref release);
						return StInternalMethods.SetEvent (swhandle.DangerousGetHandle ());
					} finally {
						if (release) {
							swhandle.DangerousRelease ();
						}
					}
				}

				RuntimeHelpers.PrepareConstrainedRegions ();
				try { }
				finally {
					if (TryAdvanceHead (h, hn)) {
						StParker pk;
						if ((pk = hn.parker).TryLock ()) {
							pk.Unpark (hn.waitKey);

							/*
							 * If this is a wait-any wait block, we are done;
							 * otherwise, keep trying to release other waiters.
							 */

							done = hn.waitType == WaitType.WaitAny;
						}
					}
				}
			} while (!done);

			return true;
		}

		protected override bool EnqueueWaiter (StWaitBlock wb, out StWaitBlock pred)
		{
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

				if (t == INFLATED) {
					pred = INFLATED;
					return false;
				}

				if (Interlocked.CompareExchange (ref t.next, wb, null) == null) {
					AdvanceTail (t, wb);
					pred = t;
					return true;
				}
			} while (true);
		}

		internal override StWaitBlock _Inflate ()
		{
			do {
				StWaitBlock t = tail;
				StWaitBlock tn = t.next;

				if (tn == SET && Interlocked.CompareExchange (ref t.next, INFLATED, SET) == SET) {
					bool release = false;
					try {
						swhandle.DangerousAddRef (ref release);
						StInternalMethods.SetEvent (swhandle.DangerousGetHandle ());
					} finally {
						if (release) {
							swhandle.DangerousRelease ();
						}
					}

					return null;
				}

				if (tn == null && Interlocked.CompareExchange (ref t.next, INFLATED, null) == null) {
					AdvanceTail (t, INFLATED);
					return t == head ? null : UnlinkPendingThreads ();
				}

				AdvanceTail (t, tn);
			} while (true);
		}

		internal override IntPtr _CreateNativeObject()
		{
			return StInternalMethods.CreateEvent (IntPtr.Zero, false, false, null);
		}
		  
		/*
		 * Here we compete with a possible releaser to unlink the waiting threads.
		 * As happens naturally with the inflate operation, we do not keep the order
		 * of the waiting threads when moving them to the native object. Also, it is
		 * possible that consecutive set operations are conflated into just one, thus
		 * freeing just one thread. This is fine as it conforms to the specification.
		 */

		private StWaitBlock UnlinkPendingThreads ()
		{
			var queue = new WaitBlockQueue ();

			do {
				StWaitBlock h = head;
				StWaitBlock hn = h.next;

				if (hn == INFLATED) {
					return queue.head;
				}
				
				if (TryAdvanceHead (h, hn)) {
					queue.Enqueue (hn);
				}
			} while (true);
		}
	}
}