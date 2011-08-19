//
// System.Threading.RegisteredWaitHandle.cs
//
// Author:
//   Dick Porter (dick@ximian.com)
//   Lluis Sanchez Gual (lluis@ideary.com)
//   Duarte Nunes (duarte.m.nunes@gmail.com)
//
// (C) Ximian, Inc.  http://www.ximian.com//
//
// Copyright (C) 2004, 2005 Novell, Inc (http://www.novell.com)
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//

using System.Runtime.InteropServices;

#pragma warning disable 0420

namespace System.Threading
{
	[ComVisible (true)]
	public sealed class RegisteredWaitHandle
#if !MOONLIGHT
		: MarshalByRefObject
#endif
	{
		private const int INACTIVE = 0;
		private const int ACTIVE = 1;
		private const int UNREGISTERING = 2;
		private const int UNREGISTERED = 3;

		private volatile int state;
		
		private CbParker cbparker;
		private StWaitBlock waitBlock;
      private StWaitBlock hint;

      private WaitHandle waitObject;
      private WaitOrTimerCallback callback;
		private object cbState;
		private readonly int timeout;
		private bool executeOnlyOnce;

		private int cbtid;
		private WaitHandle notificationObject;

		private ManualResetEvent unregisterEvent;

		internal RegisteredWaitHandle (WaitHandle waitObject, WaitOrTimerCallback callback, 
												 object cbState, int timeout, bool executeOnlyOnce)
		{
			this.waitObject = waitObject;
			this.callback = callback;
			this.cbState = cbState;
			this.timeout = timeout;
         this.executeOnlyOnce = executeOnlyOnce;

			StWaitable waitable;
			if ((waitable = waitObject.waitable) == null) {

				/*
				 * Either we're dealing with a disposed wait handle
				 * or with some other derived class.
				 */
 
				UnparkCallback (StParkStatus.Inflated);
				return;
			}

			cbparker = new CbParker (UnparkCallback, false, true);
			int ignored = 0;
         waitBlock = waitable._WaitAnyPrologue (cbparker, StParkStatus.Success, ref hint, ref ignored);

			state = ACTIVE;
         int ws = cbparker.EnableCallback (timeout);
         if (ws != StParkStatus.Pending) {
				UnparkCallback (ws);
	      }
		}

		private void UnparkCallback (int ws)
		{
			var waitable = waitObject.waitable;

			if (ws == StParkStatus.Cancelled) {
				waitable._CancelAcquire (waitBlock, hint);
		      return;
	      }

			ThreadPool.QueueUserWorkItem (_ =>
			{
				do {
					if (ws == StParkStatus.Inflated) {
						InflatedWait ();
						return;
					}
					
					if (ws != StParkStatus.Success) {
						waitable._CancelAcquire (waitBlock, hint);
					}

					if (ws != StParkStatus.Cancelled) {
						cbtid = Thread.CurrentThread.ManagedThreadId;
						callback (cbState, ws == StParkStatus.Timeout);
						cbtid = 0;
					}

					if (executeOnlyOnce || ws == StParkStatus.Cancelled) {
						FinishExecution ();
						return;
					}

					/*
					 * We must re-register with the waitable. So, initialize the 
					 * parker and execute the WaitAny prologue.
					 */

					cbparker.Reset (1);
					Thread.MemoryBarrier ();

					if (state != ACTIVE) {
						if (cbparker.TryCancel ()) {
							cbparker.Unpark (StParkStatus.Cancelled);
						}
						WaitUntilUnregistered ();
						Deactivate ();
						return;
					}
					
					int ignored = 0;
					waitBlock = waitable._WaitAnyPrologue (cbparker, StParkStatus.Success, ref hint,
					                                       ref ignored);

					ws = cbparker.EnableCallback (timeout);

					if (ws == StParkStatus.Pending) {
						return;
					}

					/*
					 * The waitable was already signalled. So, execute the unpark
					 * callback inline.
					 */ 

				} while (true);
			});
      }

		private void InflatedWait ()
		{
			Interlocked.Exchange (ref unregisterEvent, new ManualResetEvent (false));

			if (state != ACTIVE) {
				WaitUntilUnregistered ();
				Deactivate ();
				((IDisposable)unregisterEvent).Dispose ();
				return;
			}

			do {
				int ws;
				bool interrupted = false;
				do {
					try {
						ws = WaitHandle.WaitAny (new[] { waitObject, unregisterEvent });
						break;
					} catch (ThreadInterruptedException) {
						interrupted = true;
					}
				} while (true);

				if (interrupted) {
					Thread.CurrentThread.Interrupt ();
				}

				if (ws == 1) {
					Deactivate ();
					((IDisposable)unregisterEvent).Dispose ();
					return;
				}

				cbtid = Thread.CurrentThread.ManagedThreadId;
				callback (cbState, ws == WaitHandle.WaitTimeout);
				cbtid = 0;

				if (executeOnlyOnce) {
					FinishExecution ();
					((IDisposable)unregisterEvent).Dispose ();
					return;
				}
			} while (true);
		}

		private void WaitUntilUnregistered ()
		{
#if NET_4_0
			var spinWait = new SpinWait ();
#endif
			while (state != UNREGISTERED) {
#if NET_4_0
				spinWait.SpinOnce ();
#else
				Thread.SpinWait (1);
#endif
			}
		}

		private void FinishExecution ()
		{
			do {
				int oldState;
				if ((oldState = state) == UNREGISTERED) {
					break;
				}
							
				if (oldState == UNREGISTERING) {
					WaitUntilUnregistered ();
					break;
				} 

				if (Interlocked.CompareExchange (ref state, INACTIVE, ACTIVE) == ACTIVE) {
					break;
				}
			} while (true);
						
			Deactivate ();
		}

		[ComVisible (true)]
		public bool Unregister (WaitHandle waitObject)
		{
			if (state != ACTIVE || Interlocked.CompareExchange (ref state, UNREGISTERING, ACTIVE) != ACTIVE) {
				return false;
			}

			notificationObject = waitObject;

			if (cbtid == Thread.CurrentThread.ManagedThreadId) {
				state = UNREGISTERED;
				executeOnlyOnce = true;
				return true;
			}

			/*
			 * Avoid a release-followed-by-acquire-hazzard. We usually don't need
			 * an atomic instruction before a call to TryLock because the parker is 
			 * usually one-shot. However, we must be mindful of the load in TryLock 
			 * when some thread calls Reset on the same parker.
			 */

			Interlocked.Exchange (ref state, UNREGISTERED);
			
			if (cbparker != null && cbparker.TryCancel ()) {
				cbparker.Unpark (StParkStatus.Cancelled); 
				Deactivate ();
			} else if (unregisterEvent != null) {
				unregisterEvent.Set ();
			}

			return true;
		}

		private void Deactivate ()
		{
			state = INACTIVE;

			waitObject = null;
			waitBlock = null;
			hint = null;
			callback = null;
			cbparker = null;
			cbState = null;

			WaitHandle no;
			if ((no = notificationObject) != null) {
				notificationObject = null;
				no.Signal ();
			}
		}

#if ONLY_1_1
		[MonoTODO]
		~RegisteredWaitHandle() {
			// FIXME
		}
#endif
	}
}
