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

 #if NET_2_0
 
#pragma warning disable 0420

namespace System.Threading 
{
	internal sealed class StSemaphore : StWaitable
	{
		private volatile int state;
		private LockedWaitQueue queue;

		private readonly int maximumCount;

		private readonly int spinCount;

		internal StSemaphore (int count, int maximumCount, int spinCount)
		{
			if (count < 0 || count > maximumCount) {
				throw new ArgumentException ("\"count\": incorrect value");
			}
			if (maximumCount <= 0) {
				throw new ArgumentException ("\"maximumCount\": incorrect value");
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

		internal bool Wait (int acquireCount, StCancelArgs cargs)
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
			StCancelArgs.ThrowIfException (ws);
			return false;
		}

		internal void Wait (int acount)
		{
			Wait (acount, StCancelArgs.None);
		}

		internal int Release(int releaseCount)
		{
			if (releaseCount < 1) {
				throw new ArgumentOutOfRangeException("releaseCount");
			}

			int prevCount = state;
			if (!ReleaseInternal (releaseCount)) {
				//throw new SemaphoreFullException ();
			}
			if (IsReleasePending) {
				ReleaseWaitersAndUnlockQueue ();
			}
			return prevCount;
		}

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
			}
			while (true);
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
			}
			while (true);
		}

		private void UndoAcquire (int undoCount)
		{
			do {
				int s = state;
				if (Interlocked.CompareExchange (ref state, s + undoCount, s) == s) {
					return;
				}
			}
			while (true);
		}

		private bool ReleaseInternal (int releaseCount)
		{
			do {
				int s;
				int ns = (s = state) + releaseCount;
				if (ns < 0 || ns > maximumCount) {
					return false;
				}
				if (Interlocked.CompareExchange (ref state, ns, s) == s) {
					return true;
				}
			}
			while (true);
		}

		private bool IsReleasePending {
			get
			{
				StWaitBlock w = queue.First;
				return (w != null && (state >= w.request || w.parker.IsLocked) && queue.TryLock ());
			}
		}

		private void ReleaseWaitersAndUnlockQueue ()
		{
			do {
				StWaitBlock qh = queue.head;
				StWaitBlock w;
				while (state > 0 && (w = qh.next) != null) {
					StParker pk = w.parker;
					if (w.waitType == WaitType.WaitAny) {

						//
						// Try to acquire the requested permits on behalf of the
						// queued waiter.
						//

						if (!TryAcquireInternalQueued (w.request)) {
							break;
						}


						if (pk.TryLock ()) {
							pk.Unpark (w.waitKey);
						}
						else {
							UndoAcquire (w.request);
						}
					}
					else {

						//
						// Wait-all: since that the semaphore seems to have at least
						// one available permit, lock the parker and, if this is the last
						// cooperative release, unpark its owner thread.
						//

						if (pk.TryLock ()) {
							pk.Unpark (w.waitKey);
						}
					}

					//
					// Remove the wait block from the semaphore's queue,
					// marking the previous head as unlinked, and advance 
					// the head of the local queues.
					//

					qh.next = qh;
					qh = w;
				}

				//
				// It seems that no more waiters can be released; so,
				// set the new semaphore queue's head and unlock it.
				//

				queue.SetHeadAndUnlock (qh);

				//
				// If, after the semaphore's queue is unlocked, it seems
				// that more waiters can be released, repeat the release
				// processing.
				//

				if (!IsReleasePending) {
					return;
				}
			}
			while (true);
		}

		private void CancelAcquire (StWaitBlock wb)
		{
			//
			// If the wait block is still linked and it isn't the last wait block
			// of the queue and the queue's lock is free unlink the wait block.
			//

			StWaitBlock wbn;
			if ((wbn = wb.next) != wb && wbn != null && queue.TryLock ()) {
				queue.Unlink (wb);
				ReleaseWaitersAndUnlockQueue ();
			}
		}

		private int EnqueueAcquire (StWaitBlock wb, int acquireCount)
		{
			bool isFirst = queue.Enqueue (wb);

			//
			// If the wait block was inserted at front of the semaphore's
			// queue, re-check if the current thread is at front of the
			// queue and can now acquire the requested permits; if so,
			// try lock the queue and execute the release processing.
			//

			if (isFirst && state >= acquireCount && queue.TryLock ()) {
				ReleaseWaitersAndUnlockQueue ();
			}

			return (isFirst ? spinCount : 0);
		}

		internal override bool _AllowsAcquire
		{
			get { return (state != 0 && queue.IsEmpty); }
		}

		internal override bool _TryAcquire ()
		{
			return TryAcquireInternal (1);
		}

		internal override bool _Release ()
		{
			if (!ReleaseInternal (1)) {
				return false;
			}
			if (IsReleasePending) {
				ReleaseWaitersAndUnlockQueue ();
			}
			return true;
		}

		internal override StWaitBlock _WaitAnyPrologue (StParker pk, int key,
														ref StWaitBlock ignored, ref int sc)
		{
			if (TryAcquireInternal (1)) {
				if (pk.TryLock ()) {
					pk.UnparkSelf (key);
				}
				else {
					UndoAcquire (1);
					if (IsReleasePending) {
						ReleaseWaitersAndUnlockQueue ();
					}
				}

				//
				// Return null because no wait block was inserted on the
				// semaphore's wait queue.
				//

				return null;
			}

			var wb = new StWaitBlock (pk, WaitType.WaitAny, 1, key);
			sc = EnqueueAcquire (wb, 1);
			return wb;
		}

		internal override StWaitBlock _WaitAllPrologue (StParker pk, ref StWaitBlock ignored,
														ref int sc)
		{
			if (_AllowsAcquire) {
				if (pk.TryLock ()) {
					pk.UnparkSelf (StParkStatus.StateChange);
				}
				return null;
			}

			var wb = new StWaitBlock (pk, WaitType.WaitAll, 1, StParkStatus.StateChange);
			sc = EnqueueAcquire (wb, 1);

			return wb;
		}

		internal override void _UndoAcquire ()
		{
			UndoAcquire (1);
			if (IsReleasePending) {
				ReleaseWaitersAndUnlockQueue ();
			}
		}

		internal override void _CancelAcquire (StWaitBlock wb, StWaitBlock ignored)
		{
			CancelAcquire (wb);
		}
	}
}

#endif