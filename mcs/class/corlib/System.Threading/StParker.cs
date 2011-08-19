//
// System.Threading.StParker.cs
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
using System.Runtime.ConstrainedExecution;
using Microsoft.Win32.SafeHandles;

#pragma warning disable 0420

namespace System.Threading 
{
   public class StParker 
	{
      private const int WAIT_IN_PROGRESS_BIT = 31;
      private const int WAIT_IN_PROGRESS = (1 << WAIT_IN_PROGRESS_BIT);
      private const int LOCK_COUNT_MASK = (1 << 16) - 1;

      internal volatile StParker pnext;
      internal volatile int state;
   	internal IntPtr parkSpot = IntPtr.Zero;
      internal int waitStatus;

      internal StParker (int releasers) 
		{
         state = releasers | WAIT_IN_PROGRESS;
      }

      internal StParker () 
			: this (1) { }
		
      internal bool IsLocked
      {
         get { return (state & LOCK_COUNT_MASK) == 0; }
      }

      internal void Reset (int releasers) 
		{
         pnext = null;
         state = releasers | WAIT_IN_PROGRESS;
      }

      internal void Reset () 
		{
         Reset (1);
      }

   	internal bool TryLock (int n) 
		{
         do {
				int s;
            if (((s = state) & LOCK_COUNT_MASK) == 0) {
					return false;
            }

            if (Interlocked.CompareExchange (ref state, s - n, s) == s) { // No need to validate if s >= n
					return (s & LOCK_COUNT_MASK) == 1;
            }
         } while (true);
      }

   	internal bool TryLock ()
   	{
   		return TryLock (1);
   	}

   	internal bool TryCancel () 
		{
         do {
				int s;
            if (((s = state) & LOCK_COUNT_MASK) <= 0) {
               return false;
            }

            if (Interlocked.CompareExchange (ref state, (s & WAIT_IN_PROGRESS), s) == s) {
               return true;
            }
			} while (true);
      }

      internal void SelfCancel () 
		{
         state = WAIT_IN_PROGRESS;
      }

      internal bool UnparkInProgress (int ws) 
		{
         waitStatus = ws;
         return (state & WAIT_IN_PROGRESS) != 0 &&
                (Interlocked.Exchange (ref state, 0) & WAIT_IN_PROGRESS) != 0;
      }

		internal virtual void Unpark (int status) 
		{
			if (UnparkInProgress (status)) {
				return;
			}
			StInternalMethods.Set_internal (parkSpot);
		}

      internal void UnparkSelf (int status) 
		{
         waitStatus = status;
         state = 0;
      }

		/*
		 * Parks the current thread, activating the specified cancellers. We use
		 * a CER to reliably free the park spot, which will be reclaimed when the 
		 * thread exits. The method is hardened against ThreadAbortExceptions. If 
		 * one is thrown and the parker object cannot be cancelled (the unparking  
		 * thread already called TryLock and is on its way to call Unpark), we must
		 * park the thread. Although we are blocking the thread inside a CER, the 
		 * waiting time is bounded since we reliably execute all TryLock/Unpark pairs.
		 * Even if the AD is being unloaded we must park the thread in order to ensure 
		 * that Unpark uses a valid park spot.
		 */

		[ReliabilityContract(Consistency.WillNotCorruptState, Cer.MayFail)]
		internal int Park (int spinCount, StWaitable[] waitables, int inflatedIndexes, 
								 int inflatedCount, StCancelArgs cargs) 
		{
#if NET_4_0
			var spinWait = new SpinWait ();
#endif
			do {
				if (state == 0) {
					return waitStatus;
				}
#if NET_4_0
				if (cargs.CancellationToken.IsCancellationRequested && TryCancel ()) {
					return StParkStatus.Cancelled;
				}
#endif
				if (spinCount-- <= 0) {
					break;
				}
#if NET_4_0
				spinWait.SpinOnce ();
#else
			Thread.SpinWait (1);
#endif
			} while (true);

			RuntimeHelpers.PrepareConstrainedRegions ();
			try
			{
				StInternalMethods.Alloc_internal (ref parkSpot);

				if (parkSpot == IntPtr.Zero) {
					
					/*
					 * FIXME: What's the right thing to do here?
					 */
					
					throw new OutOfMemoryException ();
				}
				
				if (!TestAndClearInProgress ()) {
					return waitStatus;
				}

				bool interrupted = false;

#if NET_4_0 || MOBILE
				bool unregister = false;

				if (cargs.CancellationToken.CanBeCanceled && 
					 !(unregister = cargs.CancellationToken.cts.RegisterParker (this))) {
					
					if (TryCancel ()) {
						return StParkStatus.Cancelled;
					}

					cargs = StCancelArgs.None;
				}
#endif

				int ws = inflatedCount == 0
						 ? ParkSingle (cargs, ref interrupted)
						 : ParkMultiple (waitables, inflatedIndexes, inflatedCount, cargs);

				if (ws != StParkStatus.Success) {
					if (TryCancel ()) {
						UnparkSelf(ws > StParkStatus.Success ? GetWaitAnyIndex (inflatedIndexes, ws) : ws);
					} else {
						if (ws > StParkStatus.Success) {
							waitables[GetWaitAnyIndex (inflatedIndexes, ws)]._UndoAcquire ();
						}

						do {
							try {
								StInternalMethods.WaitForParkSpot_internal (parkSpot, Timeout.Infinite);
								break;
							} catch (Exception) { }
						} while (true);
					}
				}

#if NET_4_0
				if (unregister) {
					cargs.CancellationToken.cts.DeregisterParker (this);
				}
#endif
				
				return waitStatus;
			} finally {
				if (parkSpot != IntPtr.Zero) {
					StInternalMethods.Free_internal (parkSpot);
				}
			}
		}

		private int ParkSingle (StCancelArgs cargs, ref bool interrupted)
		{
			do {
				try {
					return StInternalMethods.WaitForParkSpot_internal (parkSpot, cargs.Timeout)
						  ? StParkStatus.Success
						  : StParkStatus.Timeout;
				} catch (ThreadInterruptedException) {
					if (interrupted) {
						return StParkStatus.Interrupted;
					}
					interrupted = true;
				}
			} while (true);
		}

   	private int ParkMultiple (StWaitable[] waitables, int inflatedIndexes, int inflatedCount,
   	                          StCancelArgs cargs)
   	{
			var handles = new SafeWaitHandle[inflatedCount];
			var refs = new bool[inflatedCount];

			try
			{
   			for (int i = 0, handleIdx = 0; i < waitables.Length; ++i) {
   				if ((inflatedIndexes & (1 << i)) != 0) {
						var swhandle = waitables [i].swhandle;
						swhandle.DangerousAddRef (ref refs [handleIdx]);
   					handles [handleIdx++] = swhandle;
   				}
   			}

				int res = StInternalMethods.WaitMultiple_internal (parkSpot, handles, false, cargs.Timeout);
				return res == WaitHandle.WaitTimeout ? StParkStatus.Success : StParkStatus.Timeout;
			} finally {
				for (int i = 0; i < handles.Length; ++i) {
					if (refs [i]) {
						handles [i].DangerousRelease ();
					}
				}
			}
   	}

   	internal int Park (int spinCount, StCancelArgs cargs)
      {
      	return Park (spinCount, null, 0, 0, cargs);
      }

      internal int Park(StCancelArgs cargs) 
		{
			return Park (0, null, 0, 0, cargs);
      }

      internal int Park () 
		{
			return Park (0, null, 0, 0, StCancelArgs.None);
      }

   	internal bool TestAndClearInProgress () 
		{
         do {
            int s;
            if ((s = state) >= 0) {
               return false;
            }
            if (Interlocked.CompareExchange (ref state, (s & ~WAIT_IN_PROGRESS), s) == s) {
               return true;
            }
			} while (true);
      }

		private static int GetWaitAnyIndex (int inflatedIndexes, int ws)
		{
			int index = -1;
			do {
				if ((inflatedIndexes & (1 << ++index)) != 0) {
   				ws -= 1;
   			}
			} while (ws > 0);
   		return index;
   	}

      internal bool CasNext (StParker n, StParker nn) 
		{
			return pnext == n &&
                Interlocked.CompareExchange (ref pnext, nn, n) == n;
      }
	}

   internal delegate void ParkerCallback (int waitStatus);

	internal sealed class CbParker : StParker {
      private readonly ParkerCallback callback;
      private Timer timer;
		private readonly SynchronizationContext sctx;
		private readonly ExecutionContext ectx;

		internal CbParker (ParkerCallback pkcb, bool useSynchronizationContext,
                         bool useExecutionContext) : base (1) 
		{
         callback = pkcb;

			if (useSynchronizationContext) {
				sctx = SynchronizationContext.Current;
			}

			if (useExecutionContext) {
				ectx = ExecutionContext.Capture ();
			}
      }
	
		internal override void Unpark (int status)
		{
			if (timer != null && status != StParkStatus.Timeout) {
				timer.Change (-1, -1);
			}

			waitStatus = status;

			if (sctx != null) {
            sctx.Send (toSynchContext, this);
         } else {
            ExecuteCallback ();
         }
		}

		private static readonly SendOrPostCallback toSynchContext =
         arg => ((CbParker) arg).ExecuteCallback ();

      private static readonly ContextCallback toExecContext = arg =>
      {
      	var cbparker = (CbParker) arg;
         cbparker.callback (cbparker.waitStatus);
      };

      internal void ExecuteCallback ()
      {
         if (ectx != null) {
            ExecutionContext.Run (ectx, toExecContext, this);
         } else {
            callback (waitStatus);
         }
      }

		private static readonly TimerCallback onTimeout = arg =>
      {
         var cbparker = (CbParker) arg;
         if (cbparker.TryCancel ()) {
				cbparker.Unpark (StParkStatus.Timeout);
         }
      };

      internal int EnableCallback (int timeout) 
		{
	      /*
	       * If the unpark method was already called, return immediately.
	       */

	      if (state >= 0) {
		      return waitStatus;
	      }

	      if (timeout == 0) {
            if (TryCancel ()) {
               return (waitStatus = StParkStatus.Timeout);
            }
         } else if (timeout != Timeout.Infinite) {
				if (timer == null) {
					timer = new Timer (onTimeout, this, Timeout.Infinite, Timeout.Infinite);
				}
         	timer.Change (timeout, Timeout.Infinite);
         }

	      if (!TestAndClearInProgress ()) {
            if (waitStatus != StParkStatus.Timeout) {
               timer.Change (Timeout.Infinite, Timeout.Infinite);
            }
            return waitStatus;
         }

         return StParkStatus.Pending;
      }
   }

   internal class SentinelParker : StParker 
	{
      internal SentinelParker () : base (0) { }
   }
}
