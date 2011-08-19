//
// System.Threading.StSemaphore.cs
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
	public sealed class StSemaphore : StWaitable {
		private const int SEM_INFLATED = -1;

		private volatile int state;
		private LockedWaitQueue queue;
		private readonly int maximumCount;
		private readonly int spinCount;

		public StSemaphore (int count, int maximumCount, int spinCount)
		{
			if (count < 0 || count > maximumCount) {
				throw new ArgumentOutOfRangeException ("count");
			}

			if (maximumCount <= 0) {
				throw new ArgumentOutOfRangeException ("maximumCount");
			}
			
			queue.Init ();
			state = count;
			this.maximumCount = maximumCount;
			this.spinCount = Environment.ProcessorCount > 0 ? spinCount : 0;
		}

		internal StSemaphore (int count, int maximumCount)
			: this (count, maximumCount, 0) { }

		internal StSemaphore (int count)
			: this (count, Int32.MaxValue, 0) { }

		internal int CurrentCount {
			get { return state; }
		}
				
		internal override bool _AllowsAcquire {
			get { return state > 0 && queue.IsEmpty; }
		}
		
		/*
		 * If at least one waiter can be released, or the first is waiter is locked,
		 * returns true with the semaphore's queue locked; otherwise returns false.
		 */

		private bool IsReleasePending {
			get {
				StWaitBlock w = queue.First;
				return w != null && (state >= w.request || w.parker.IsLocked) && queue.TryLock();
			}
		}

		/*
		 * No need to worry about inflating as this is not called by Semaphore.
		 */

		internal bool TryWait (int acquireCount, StCancelArgs cargs)
		{
			if (acquireCount <= 0 || acquireCount > maximumCount) {
				throw new ArgumentException ("acquireCount");
			}

			if (TryAcquireInternal (acquireCount)) {
				return true;
			}

			if (cargs.Timeout == 0) {
				return false;
			}

			var wb = new StWaitBlock (WaitType.WaitAny, acquireCount);
			int sc = EnqueueAcquire (wb, acquireCount);

			int ws = wb.parker.Park (sc, cargs);
			if (ws == StParkStatus.Success) {
				return true;
			}

			CancelAcquire (wb);
			cargs.ThrowIfException (ws);
			return false;
		}

		internal void Wait (int acount)
		{
			TryWait (acount, StCancelArgs.None);
		}

		internal int Release (int releaseCount)
		{
			if (releaseCount < 1) {
				throw new ArgumentOutOfRangeException("releaseCount");
			}

			int prevCount;
			if (!ReleaseInternal (releaseCount, out prevCount)) {
				throw _SignalException;
			}
			return prevCount;
		}
		
		internal override bool _TryAcquire ()
		{
			return TryAcquireInternal (1);
		}
		
		internal override bool _Release ()
		{
			int ignored;
			return ReleaseInternal (1, out ignored);
		}

		internal override StWaitBlock _Inflate()
		{
#if NET_4_0 
			var spinWait = new SpinWait ();
#endif

			while (!queue.TryLock ()) {
#if NET_4_0 								
				spinWait.SpinOnce ();
#else
				Thread.SpinWait (1);
#endif		
			}

			int currentCount = Interlocked.Exchange (ref state, SEM_INFLATED);
			
			int ignored;
			StInternalMethods.ReleaseSemaphore (swhandle.DangerousGetHandle (), currentCount, out ignored);
			
			StWaitBlock wb = queue.head;
			queue.head = queue.tail = null; // Synchronize with a pending Enqueue.
			return wb;
		}

		internal override IntPtr _CreateNativeObject()
		{
			return StInternalMethods.CreateSemaphore (IntPtr.Zero, 0, maximumCount, null);
		}

		internal override StWaitBlock _WaitAnyPrologue (StParker pk, int key,
																		ref StWaitBlock hint, ref int sc)
		{
			return TryAcquireInternal (1)
				  ? null
			     : WaitPrologue(pk, WaitType.WaitAny, key, ref hint, ref sc);
		}

		internal override StWaitBlock _WaitAllPrologue (StParker pk, ref StWaitBlock hint,
																		ref int sc) {
			return _AllowsAcquire
				  ? null
			     : WaitPrologue(pk, WaitType.WaitAll, StParkStatus.StateChange, ref hint, ref sc);
		}

		private StWaitBlock WaitPrologue (StParker pk, WaitType type, int key, 
													 ref StWaitBlock hint, ref int sc) 
		{
			if (state == SEM_INFLATED) {
				hint = INFLATED;
				return null;
			}

			var wb = new StWaitBlock (pk, type, 1, key);
			sc = EnqueueAcquire (wb, 1);
			
			if (state == SEM_INFLATED && pk.TryCancel ()) {
				pk.UnparkSelf (StParkStatus.Inflated);
				hint = INFLATED;
				return null;
			}

			return wb;
		}

		internal override void _UndoAcquire ()
		{
			UndoAcquire (1);
			if (IsReleasePending) {
				ReleaseWaitersAndUnlockQueue (null);
			}
		}

		internal override void _CancelAcquire (StWaitBlock wb, StWaitBlock ignored)
		{
			CancelAcquire (wb);
		}

#if NET_4_0
		internal override Exception _SignalException {
			get { return new SemaphoreFullException (); }
		}
#else
		public delegate Exception SignalExceptionFactory();
		public SignalExceptionFactory SignalException { get; set; }

		internal override Exception _SignalException {
			get { return SignalException(); }
		}
#endif

		private bool TryAcquireInternal (int acquireCount)
		{
			do {
				int s;
				int ns = (s = state) - acquireCount;

				if (ns < 0 || !queue.IsEmpty) {
					return false;
				}
				if (Interlocked.CompareExchange (ref state, ns, s) == s) {
					return true;
				}
			} while (true);
		}

		private bool TryAcquireInternalQueued (int acquireCount)
		{
			do {
				int s;
				int ns = (s = state) - acquireCount;
				if (ns < 0) {
					return false;
				}
				if (Interlocked.CompareExchange (ref state, ns, s) == s) {
					return true;
				}
			} while (true);
		}

		private void ReleaseWaitersAndUnlockQueue(StWaitBlock self) {
			do {
				StWaitBlock qh = queue.head;
				StWaitBlock  w;

				while (state > 0 && (w = qh.next) != null) {
					StParker pk = w.parker;

					if (w.waitType == WaitType.WaitAny) {
						if (!TryAcquireInternalQueued (w.request)) {
							break;
						}

						if (pk.TryLock ()) {
							if (w == self) {
								pk.UnparkSelf (w.waitKey);
							} else {
								pk.Unpark (w.waitKey);
							}
						} else {
							UndoAcquire (w.request);
						}
					} else if (pk.TryLock ()) {
						if (w == self) {
							pk.UnparkSelf (w.waitKey);
						} else {
							pk.Unpark (w.waitKey);
						}
					}

					qh.next = qh;
					qh = w;
				}

				queue.SetHeadAndUnlock (qh);
			} while (IsReleasePending);
		}

		private bool ReleaseInternal (int releaseCount, out int prevCount)
		{
			do {
				int ns = (prevCount = state) + releaseCount;

				if (prevCount == SEM_INFLATED) {

					/*
					 * We must wait until the inflation is complete so that we don't compete 
					 * with the inflater by releasing permits that could fill the semaphore.
					 */

#if NET_4_0 
					var spinWait = new SpinWait ();
#endif

					while (queue.head != null) {
#if NET_4_0
						spinWait.SpinOnce ();
#else
						Thread.SpinWait (1);
#endif
					}
					
					bool release = false;
					try {
						swhandle.DangerousAddRef (ref release);
						return StInternalMethods.ReleaseSemaphore (swhandle.DangerousGetHandle (), releaseCount,
						                                           out prevCount);
					} finally {
						if (release) {
							swhandle.DangerousRelease ();
						}
					}
				}

				if (ns < 0 || ns > maximumCount) {
					return false;
				}

				if (Interlocked.CompareExchange (ref state, ns, prevCount) == prevCount) {
					if (IsReleasePending) {
						ReleaseWaitersAndUnlockQueue (null);
					}
					return true;
				}
			} while (true);
		}

		private int EnqueueAcquire (StWaitBlock wb, int acquireCount)
		{
			bool isFirst = queue.Enqueue (wb);

			/*
			 * If the wait block was inserted at the front of the queue and
			 * the current thread can now acquire the requested permits, try 
			 * to lock the queue and execute the release processing.
			 */

			if (isFirst && state >= acquireCount && queue.TryLock ()) {
				ReleaseWaitersAndUnlockQueue (wb);
			}

			return isFirst ? spinCount : 0;
		}
		
		private void UndoAcquire (int undoCount)
		{
			do {
				int s = state;

				if (s == SEM_INFLATED) {
					int ignored;
					bool release = false;
					try {
						swhandle.DangerousAddRef (ref release);
						StInternalMethods.ReleaseSemaphore(swhandle.DangerousGetHandle(), undoCount, out ignored);
					} finally {
						if (release) {
							swhandle.DangerousRelease ();
						}
					}
					return;
				}

				if (Interlocked.CompareExchange (ref state, s + undoCount, s) == s) {
					return;
				}
			} while (true);
		}

		private void CancelAcquire (StWaitBlock wb)
		{
			/*
			 * If the wait block is still linked and it isn't the last wait block
			 * of the queue and the queue's lock is free unlink the wait block.
			 */

			StWaitBlock wbn;
			if ((wbn = wb.next) != wb && wbn != null && queue.TryLock ()) {
				queue.Unlink (wb);
				ReleaseWaitersAndUnlockQueue (null);
			}
		}
	}
}
