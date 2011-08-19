//
// System.Threading.StNotificationEvent.cs
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
	internal abstract class StNotificationEventBase : StWaitable
   {
      internal static readonly StWaitBlock SET = StWaitBlock.SENTINEL;

      /*
       * The state of the event and the event's queue (i.e., a non-blocking
       * stack) are stored on the *state* field as follows:
       * - *state* == SET: the event is signalled;
       * - *state* == null: the event is non-signalled the queue is empty;
       * - *state* != null && *state* != SET: the event is non-signalled
		 *                                      and its queue is non-empty;
		 * - *state* == INFLATED: the object is inflated.                                     
		 */

      internal volatile StWaitBlock state;
      internal readonly int spinCount;

      internal StNotificationEventBase (bool initialState, int sc)
      {
			id = NOTIFICATION_EVENT_ID;
         state = initialState ? SET : null;
         spinCount = Environment.ProcessorCount > 0 ? sc : 0;
      }

      internal StNotificationEventBase (bool initialState)
         : this (initialState, 0) { }

      internal override bool _AllowsAcquire {
         get { return state == SET; }
      }

		internal bool Set ()
      {
			bool done;
			StWaitBlock p;
			do {
				if ((p = state) == SET) {
					return true;
				}

				if (p == INFLATED) {
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
					if ((done = Interlocked.CompareExchange(ref state, SET, p) == p) &&
						 p != null && p != SET) {
						
						/*
						 * If spinning is configured and there is more than one thread in the
						 * wait queue, we first release the thread that is spinning. As only 
						 * one thread spins, we maximize the chances of unparking that thread
						 * before it blocks.
						 */

						StParker pk;
						if (spinCount != 0 && p.next != null) {
							StWaitBlock pv = p, n;
							while ((n = pv.next) != null && n.next != null) {
								pv = n;
							}

							if (n != null) {
								pv.next = null;
								if ((pk = n.parker).TryLock ()) {
									pk.Unpark (n.waitKey);
								}
							}
						}
         
						do {
							if ((pk = p.parker).TryLock ()) {
								pk.Unpark (p.waitKey);
							}
						} while ((p = p.next) != null);
					}
						
				}
			} while (!done);

			return true;
      }

		internal bool Reset ()
      {
			StWaitBlock s;
			if ((s = state) == SET && (s = Interlocked.CompareExchange(ref state, null, SET)) == SET) {
				return true;
			}

			if (s == INFLATED) {
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
         return _AllowsAcquire;
      }

      internal override StWaitBlock _WaitAnyPrologue (StParker pk, int key,
                                                      ref StWaitBlock hint, ref int sc)
      {
         return WaitWithParker (pk, WaitType.WaitAny, key, ref hint, ref sc);
      }

      internal override StWaitBlock _WaitAllPrologue (StParker pk, ref StWaitBlock hint,
                                                      ref int sc)
      {
         return WaitWithParker (pk, WaitType.WaitAll, StParkStatus.StateChange, ref hint, ref sc);
      }

		private StWaitBlock WaitWithParker (StParker pk, WaitType type, int key, 
														ref StWaitBlock hint, ref int sc)
      {
         StWaitBlock wb = null;
         do {
            StWaitBlock s;
            if ((s = state) == SET) {
               return null;
            }

				if (s == INFLATED) {
					hint = INFLATED;
					return null;
				}

            if (wb == null) {
               wb = new StWaitBlock (pk, type, 0, key);
            }

            wb.next = s;
            if (Interlocked.CompareExchange (ref state, wb, s) == s) {
               sc = s == null ? spinCount : 0;
               return wb;
            }
         } while (true);
      }

		internal override StWaitBlock _Inflate()
		{
			StWaitBlock s = Interlocked.Exchange (ref state, INFLATED);
			if (s == SET) {
				StInternalMethods. SetEvent (swhandle.DangerousGetHandle ());
				return null;
			}
			return s;
		}

		internal override IntPtr _CreateNativeObject()
		{
			return StInternalMethods. CreateEvent (IntPtr.Zero, true, false, null);
		}

      internal override void _CancelAcquire (StWaitBlock wb, StWaitBlock ignored)
      {
         Unlink (wb);
      }

		internal void Unlink (StWaitBlock wb)
      {
         StWaitBlock s;
         if ((s = state) == SET || s == null || s == INFLATED ||
				 (wb.next == null && s == wb && Interlocked.CompareExchange (ref state, null, s) == s)) {
				return;
         }
         SlowUnlink (wb);
      }

      private void SlowUnlink (StWaitBlock wb)
      {
         StWaitBlock next;
         if ((next = wb.next) != null && next.parker.IsLocked) {
            next = next.next;
         }

         StWaitBlock p = state;
			StWaitBlock s;

         while (p != null && p != next && (s = state) != null && s != SET && s != INFLATED) {
            StWaitBlock n;
            if ((n = p.next) != null && n.parker.IsLocked) {
               p.CasNext (n, n.next);
            } else {
               p = n;
            }
         }
      }
   }

   internal sealed class StNotificationEvent : StNotificationEventBase
   {
      internal StNotificationEvent (bool initialState, int spinCount)
         : base (initialState, spinCount) { }

      internal StNotificationEvent (bool initialState)
         : this (initialState, 0) { }

      internal StNotificationEvent ()
         : this (false, 0) { }

      internal bool IsSet {
         get { return _AllowsAcquire; }
      }

      internal override bool _Release ()
      {
         return Set ();
      }
   }
}