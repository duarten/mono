//
// System.Threading.StLock.cs
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
	internal struct StLock
	{
		private const int FREE = 0;
		private const int BUSY = 1;
		private volatile int state;

		private volatile StWaitBlock top;

		private readonly int spinCount;

		internal StLock (int sc)
		{
			this = new StLock ();
			spinCount = Environment.ProcessorCount > 1 ? sc : 0;
		}

		internal bool TryEnter ()
		{
			return state == FREE &&
			       Interlocked.CompareExchange (ref state, BUSY, FREE) == FREE;
		}

		internal bool TryEnter (StCancelArgs cargs)
		{
			return TryEnter () || (cargs.Timeout != 0 && SlowEnter (cargs));
		}

		internal void Enter ()
		{
			TryEnter (StCancelArgs.None);
		}

		internal bool SlowEnter (StCancelArgs cargs)
		{
			int lastTime = (cargs.Timeout != Timeout.Infinite) ? Environment.TickCount : 0;
			StWaitBlock wb = null;
			do {
				int sc = spinCount;
#if NET_4_0
				var spinWait = new SpinWait ();
#endif
				do {
					if (state == FREE &&
					    Interlocked.CompareExchange (ref state, BUSY, FREE) == FREE) {
						return true;
					}
					if (top != null || sc-- <= 0) {
						break;
					}
#if NET_4_0
					spinWait.SpinOnce ();
#else
					Thread.SpinWait (1);
#endif
				} while (true);


				if (wb == null) {
					wb = new StWaitBlock (1);
				} else {
					wb.parker.Reset ();
				}

				do {
					StWaitBlock t;
					wb.next = t = top;
					if (Interlocked.CompareExchange (ref top, wb, t) == t) {
						break;
					}
				} while (true);

				if (TryEnter ()) {
					wb.parker.SelfCancel ();
					return true;
				}

				int ws = wb.parker.Park (cargs);

				if (ws != StParkStatus.Success) {
					cargs.ThrowIfException (ws);
					return false;
				}

				if (TryEnter ()) {
					return true;
				}
				
				if (!cargs.AdjustTimeout (ref lastTime)) {
					return false;
				}
			} while (true);
		}

		internal void Exit ()
		{
			/*
			 * Because atomic operations on references are more expensive than  
			 * on integers, we try to optimize the release when the wait queue 
			 * is empty. However, when the wait queue is seen as non-empty after 
			 * the lock is released, our algorithm resorts to another atomic 
			 * instruction in order to unpark pending waiters.
			 */

			if (top == null) {
				Interlocked.Exchange (ref state, FREE);
				if (top == null) {
					return;
				}
			} else {
				state = FREE;
			}

			/*
			 * Unpark all waiting threads according to their arrival order.
			 */
 
			StWaitBlock p = Interlocked.Exchange (ref top, null);
			StWaitBlock ws = null, n;
			while (p != null) {
				n = p.next;
				if (p.parker.TryLock ()) {
					p.next = ws;
					ws = p;
				}
				p = n;
			}

			while (ws != null) {
				n = ws.next;
				ws.parker.Unpark (StParkStatus.Success);
				ws = n;
			}
		}
	}
}