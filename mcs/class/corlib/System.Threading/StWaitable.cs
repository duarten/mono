//
// System.Threading.StWaitable.cs
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

using Microsoft.Win32.SafeHandles;

#pragma warning disable 0420

namespace System.Threading
{
	public abstract class StWaitable
	{
		internal static readonly StWaitBlock INFLATED = new StWaitBlock ();
		private static int idSeed = Int32.MinValue;
		internal const int NOTIFICATION_EVENT_ID = Int32.MaxValue;

		/*
       * The synchronizer ID is used to sort the synchronizers
       * in the WaitAll method in order to prevent livelock.
       */

		internal volatile int id;
		internal SafeWaitHandle swhandle;

		private int Id {
			get 
			{
				if (id == 0) {
					int nid;
					while ((nid = Interlocked.Increment (ref idSeed)) == 0 ||
					       nid == NOTIFICATION_EVENT_ID) { }
					Interlocked.CompareExchange (ref id, nid, 0);
				}
				return id;
			}
		}

		/*
       * The Waitable virtual methods.
       */

		internal abstract bool _AllowsAcquire { get; }

		internal abstract bool _TryAcquire ();

		internal virtual bool _Release ()
		{
			return false;
		}

		internal virtual StWaitBlock _Inflate () {
			throw new InvalidOperationException ();
		}

		internal virtual IntPtr _CreateNativeObject () {
			throw new InvalidOperationException ();
		}

		internal abstract StWaitBlock _WaitAnyPrologue (StParker pk, int key,
		                                                ref StWaitBlock hint, ref int sc);

		internal abstract StWaitBlock _WaitAllPrologue (StParker pk,
		                                                ref StWaitBlock hint, ref int sc);

		internal virtual void _WaitEpilogue () { }

		internal virtual void _UndoAcquire () { }

		internal virtual void _UndoInflatedAcquire () { }

		internal abstract void _CancelAcquire (StWaitBlock wb, StWaitBlock hint);

		internal virtual Exception _SignalException {
			get { return new InvalidOperationException (); }
		}

		/*
		 * The inflate methods execute within a CER.
		 */
 
		internal void Inflate ()
		{
			Inflate (new SafeWaitHandle (_CreateNativeObject (), true));
		}

		internal void Inflate (SafeWaitHandle swh)
		{
			swhandle = swh;

			StWaitBlock wb = _Inflate ();

			while (wb != null) {
				if (wb.parker.TryCancel ()) {
					wb.parker.Unpark (StParkStatus.Inflated);
				}
				wb = wb.next;
			}
		}

		internal bool TryWaitOne (StCancelArgs cargs)
		{
			if (_TryAcquire ()) {
				return true;
			}

			if (cargs.Timeout == 0) {
				return false;
			}

			var pk = new StParker ();
			StWaitBlock hint = null;
			int sc = 0;
			StWaitBlock wb;
			if ((wb = _WaitAnyPrologue (pk, StParkStatus.Success, ref hint, ref sc)) == null) {
				return hint != INFLATED || InflatedWait (cargs);
			}

			int ws = pk.Park (sc, cargs);

			if (ws == StParkStatus.Success) {
				_WaitEpilogue ();
				return true;
			}

			if (ws == StParkStatus.Inflated) {
				return InflatedWait (cargs);
			}

			_CancelAcquire (wb, hint);
			cargs.ThrowIfException (ws);
			return false;
		}

		internal bool TryWaitOne ()
		{
			return _TryAcquire ();
		}

		internal void WaitOne ()
		{
			TryWaitOne (StCancelArgs.None);
		}

		internal bool InflatedWait (StCancelArgs cargs) 
		{
			/*
			 * Waits coming from a ManualResetEventSlim are cancellable.
			 */
#if NET_4_0 || MOBILE
			using (cargs.CancellationToken.RegisterInternal (t => ((Thread)t).Interrupt (), 
																			 Thread.CurrentThread)) 
#endif
			{
				bool release = false;
				try {
					swhandle.DangerousAddRef (ref release);
					return StInternalMethods.Wait_internal (swhandle.DangerousGetHandle (), cargs.Timeout);
#if NET_4_0 || MOBILE
				} catch (ThreadInterruptedException) {
					cargs.ThrowIfException (cargs.CancellationToken.IsCancellationRequested 
						                     ? StParkStatus.Cancelled
													: StParkStatus.Interrupted);
					return false;
#endif
				} finally {
					if (release) {
						swhandle.DangerousRelease ();
					}
				}
			}
		}

		private static int InflatedWaitMultiple (StWaitable[] ws, bool waitAll, int inflatedIndexes,
															  int inflatedCount, StCancelArgs cargs)
		{
			var handles = new SafeWaitHandle [inflatedCount];
			var refs = new bool [inflatedCount];
#if NET_4_0 || MOBILE
			var reg = default (CancellationTokenRegistration);
#endif

			try {
				for (int i = 0, handleIdx = 0; i < ws.Length; ++i) {
					if ((inflatedIndexes & (1 << i)) != 0) {
						var swhandle = ws [i].swhandle;
						swhandle.DangerousAddRef (ref refs [handleIdx]);
						handles [handleIdx++] = swhandle;
					}
				}

#if NET_4_0 || MOBILE
				reg = cargs.CancellationToken.RegisterInternal (t => ((Thread) t).Interrupt (),
				                                                Thread.CurrentThread);
#endif
				int res = StInternalMethods.WaitMultiple_internal (IntPtr.Zero, handles, waitAll,
				                                                   cargs.Timeout);
				return res == WaitHandle.WaitTimeout ? StParkStatus.Success : StParkStatus.Timeout;
#if NET_4_0 || MOBILE
			} catch (ThreadInterruptedException) {
				return cargs.CancellationToken.IsCancellationRequested 
					  ? StParkStatus.Cancelled 
					  : StParkStatus.Interrupted;
#endif
			} finally {
				for (int i = 0; i < handles.Length; ++i) {
					if (refs [i]) {
						handles [i].DangerousRelease ();
					}
				}
#if NET_4_0 || MOBILE
				reg.Dispose ();
#endif
			}
		}

		internal static int WaitAny (StWaitable[] ws)
		{
			return WaitAny (ws, StCancelArgs.None);
		}

		internal static int WaitAny (StWaitable[] ws, StCancelArgs cargs)
		{
			if (ws == null) {
				throw new ArgumentNullException ("ws");
			}

			int len = ws.Length;

			for (int i = 0; i < len; i++) {
				if (ws [i]._TryAcquire ()) {
					return StParkStatus.Success + i;
				}
			}

			if (cargs.Timeout == 0) {
				return StParkStatus.Timeout;
			}

		retry:

			/*
			 * Create a parker and execute the WaitAny prologue on all
			 * waitables. We stop executing prologues as soon as we detect
			 * that the acquire operation was accomplished.
			 */ 

			var pk = new StParker (1);
			int inflated = 0;
			int inflatedCount = 0;
			var wbs = new StWaitBlock [len];
			var hints = new StWaitBlock [len];

			int lv = -1;
			int gsc = 0;

			for (int i = 0; !pk.IsLocked && i < len; i++) {
				StWaitable w = ws [i];
				int sc = 0;

				if ((wbs [i] = w._WaitAnyPrologue (pk, i, ref hints [i], ref sc)) == null) {
					if (hints [i] == INFLATED) {
						inflated |= 1 << i;
						inflatedCount += 1;
					} else {
						if (pk.TryLock ()) {
							pk.UnparkSelf (i);
						} else {
							w._UndoAcquire ();
						}
						break;
					}
				} else if (gsc < sc) {
					gsc = sc;
				}
				lv = i;
			}

			int wst = inflatedCount == len ? InflatedWaitMultiple (ws, false, inflated, inflatedCount, cargs)
					  : inflatedCount > 0 ? pk.Park (gsc, ws, inflated, inflatedCount, cargs)
					  : pk.Park (gsc, cargs);
			 
			StWaitable acq = wst >= StParkStatus.Success ? ws [wst] : null;

			/*
			 * Cancel the acquire attempt on all waitables where we executed the WaitAny
			 * prologue, except the one we acquired and the ones that are inflated.
			 */

			for (int i = 0; i <= lv; i++) {
				StWaitable w = ws [i];
				StWaitBlock wb = wbs [i];
				if (w != acq && wb != null) {
					w._CancelAcquire (wb, hints [i]);
				}
			}

			if (acq != null) {
				try {
					acq._WaitEpilogue ();
				} catch (AbandonedMutexException e) {
					e.MutexIndex = wst;
					throw;
				}
				return wst;
			}

			if (wst == StParkStatus.Inflated) {
				goto retry;
			}

			cargs.ThrowIfException (wst);
			return StParkStatus.Timeout;
		}

		/*
		 * Sorts the waitable array by the waitable id and, at the same time,
		 * check if all waitables allow an immediate acquire operation.
		 *
		 * NOTE: The notification events are not sorted, because they don't
		 *       have an acquire side-effect. The notification events are 
		 *       grouped at the end of the sorted array.
		 */

		private static int SortAndCheckAllowAcquire (StWaitable[] ws, StWaitable[] sws, out int nevts)
		{
			int i;
			StWaitable w;
			bool acqAll = true;
			int len = ws.Length;

			/*
			 * Find the first waitable that isn't a notification event,
			 * in order to start insertion sort.			 
			 */

			nevts = len;
			for (i = 0; i < len; i++) {
				w = ws [i];
				acqAll &= w._AllowsAcquire;

				/*
				 * If the current waitable is a notification event, insert
				 * it at the end of the ordered array; otherwise, insert it
				 * at the begin of the array and break the loop.
				 */

				if (w.id == NOTIFICATION_EVENT_ID) {
					sws [--nevts] = w;
				} else {
					sws [0] = w;
					break;
				}
			}

			/*
			 * If all synchronizers are notification events, return.
			 */

			if (nevts == 0) {
				return acqAll ? 1 : 0;
			}

			/*
			 * Sort the remaining synchronizers using the insertion sort
			 * algorithm but only with the non-notification event waitables.
			 */

			int k = 1;
			for (i++; i < len; i++, k++) {
				w = ws [i];
				acqAll &= w._AllowsAcquire;
				if (w.id == NOTIFICATION_EVENT_ID) {
					sws [--nevts] = w;
				} else {
					sws [k] = w;
					int j = k - 1;
					while (j >= 0 && sws [j].Id > w.Id) {
						sws [j + 1] = sws [j];
						j--;
					}

					sws [j + 1] = w;

					if (sws [k - 1] == sws [k]) {
						return -1;
					}
				}
			}
			return acqAll ? 1 : 0;
		}

		internal static bool WaitAll (StWaitable[] ws, StCancelArgs cargs)
		{
			if (ws == null) {
				throw new ArgumentNullException ("ws");
			}

			int nevts;
			int len = ws.Length;
			var sws = new StWaitable [len];

			int waitHint = SortAndCheckAllowAcquire (ws, sws, out nevts);

			if (waitHint < 0) {
				throw new DuplicateWaitObjectException ();
			}

			/*
			 * Return success if all synchronizers are notification events and are set.
			 */

			if (waitHint != 0) {
				if (nevts == 0) {
					return true;
				}
			} else if (cargs.Timeout == 0) {
				return false;
			}

			/*
			 * If a timeout was specified, get the current time in order
			 * to adjust the timeout value later, if we re-wait.
			 */

			int lastTime = (cargs.Timeout != Timeout.Infinite) ? Environment.TickCount : 0;
			StWaitBlock[] wbs = null;
			StWaitBlock[] hints = null;
			do {
				int inflated = 0;
				AbandonedMutexException ame = null;

				if (waitHint == 0) {

					if (wbs == null) {
						wbs = new StWaitBlock [len];
						hints = new StWaitBlock [len];
					}

					/*
					 * Create a parker for cooperative release, specifying as many
					 * releasers as the number of waitables. The parker is not reused
					 * because other threads may have references to it.
					 */

					var pk = new StParker (len);
					int inflatedCount = 0;

					int gsc = 1;
					int sc = 0;
					for (int i = 0; i < len; i++) {
						if ((wbs [i] = sws [i]._WaitAllPrologue (pk, ref hints [i], ref sc)) == null) {
							if (hints [i] == INFLATED) {
								inflated |= 1 << i;
								inflatedCount += 1;
							} else if (pk.TryLock ()) {
								pk.UnparkSelf (StParkStatus.StateChange);
							}
						} else if (gsc != 0) {
							if (sc == 0) {
								gsc = 0;
							} else if (sc > gsc) {
								gsc = sc;
							}
						}
					}

					if (inflatedCount > 0 && pk.TryLock (inflatedCount)) {
						pk.UnparkSelf (StParkStatus.StateChange);
					}
					
					int wst = pk.Park (gsc, cargs);

					/*
					 * We opt for a less efficient but simpler implementation instead
					 * of using the same approach as the WaitAny operation, because:
					 *  - When parking, the thread would have to call into the park spot
					 *    even if it was already unparked, since we have to wait for the
					 *    other handles as well;
					 *  - We would have to deal with cancellation in a different way,
					 *    relying on interrupts instead of the TryCancel/Unpark pair;
					 *  - The Unpark operation might not wake the target thread, which
					 *    could lead to bugs.
					 */ 

					if (wst == StParkStatus.StateChange && inflatedCount > 0) {
						if (!cargs.AdjustTimeout (ref lastTime)) {
							return false;
						}
						wst = InflatedWaitMultiple (ws, true, inflated, inflatedCount, cargs);
					}
				
					if (wst != StParkStatus.StateChange) {
						for (int i = 0; i < len; i++) {
							StWaitBlock wb = wbs [i];
							if (wb != null) {
								sws [i]._CancelAcquire (wb, hints [i]);
							}
						}

						if (wst == StParkStatus.Inflated) {
							waitHint = 0;
							continue;
						}

						cargs.ThrowIfException (wst);
						return false;
					}
				}

				/*
				 * All waitables where we inserted wait blocks seem to allow an 
				 * immediate acquire operation; so, try to acquire all non-inflated
				 * waitables that are not notification events.
				 */

				int idx;
				for (idx = 0; idx < nevts; idx++) {
					try {
						if ((inflated & (1 << idx)) == 0 && !sws[idx]._TryAcquire()) {
							break;
						}
					} catch (AbandonedMutexException e) {
						ame = e;
						ame.MutexIndex = idx;
					}
				}

				if (idx == nevts) {
					if (ame != null) {
						throw ame;
					}
					return true;
				}

				/*
				 * We failed to acquire all waitables, so undo the acquires
				 * that we did above.
				 */

				for (int i = idx + 1; i < nevts; ++i) {
					if ((inflated & (1 << idx)) != 0) {
						sws[i]._UndoAcquire ();	
					}
				}

				while (--idx >= 0) {
					sws[idx]._UndoAcquire ();
				}
				
				if (!cargs.AdjustTimeout (ref lastTime)) {
					return false;
				}

				waitHint = 0;
			} while (true);
		}

		internal static void WaitAll (StWaitable[] ws)
		{
			WaitAll (ws, StCancelArgs.None);
		}

		internal static bool SignalAndWait (StWaitable tos, StWaitable tow, StCancelArgs cargs)
		{
			if (tos == tow) {
				return true;
			}

			var pk = new StParker ();
			StWaitBlock hint = null;
			int sc = 0;
			StWaitBlock wb = tow._WaitAnyPrologue (pk, StParkStatus.Success, ref hint, ref sc);

			if (!tos._Release ()) {
				if (wb != null && pk.TryCancel ()) {
               tow._CancelAcquire (wb, hint);
            } else {
					if (wb != null) { 
						pk.Park ();
					}
					tow._UndoAcquire ();
				}

				throw tos._SignalException;
			}

			int ws;
			
			if (hint == INFLATED || (ws = pk.Park (sc, cargs)) == StParkStatus.Inflated) {
				return tow.InflatedWait (cargs);
			}

			if (ws == StParkStatus.Success) {
				tow._WaitEpilogue ();
				return true;
			}

			tow._CancelAcquire (wb, hint);
			cargs.ThrowIfException (ws);
			return false;
		}

		internal static void SignalAndWait (StWaitable tos, StWaitable tow)
		{
			SignalAndWait (tos, tow, StCancelArgs.None);
		}
	}
}