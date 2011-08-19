//
// System.Threading.WaitHandle.cs
//
// Author:
// 	Dick Porter (dick@ximian.com)
// 	Gonzalo Paniagua Javier (gonzalo@ximian.com)
//		Duarte Nunes (duarte.m.nunes@gmail.com)
//
// (C) 2002,2003 Ximian, Inc.	(http://www.ximian.com)
// Copyright (C) 2004-2005 Novell, Inc (http://www.novell.com)
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

using System.Runtime.CompilerServices;
using System.Runtime.Remoting;
using System.Runtime.Remoting.Contexts;
using System.Security.Permissions;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Runtime.ConstrainedExecution;

namespace System.Threading
{
	[ComVisible (true)]
	public abstract class WaitHandle
#if MOONLIGHT
		: IDisposable
#else
		: MarshalByRefObject, IDisposable
#endif
	{
		private static StWaitable[] CheckArray (WaitHandle[] waitHandles, bool waitAll)
		{
			if (waitHandles == null) {
				throw new ArgumentNullException ("waitHandles");
			}

			/*
				int length = handles.Length;
            if (length > 64) {
                throw new NotSupportedException ("Too many handles");
            }
          */

			if (waitHandles.Length == 0) {
				// MS throws different exceptions from the different methods.
				if (waitAll) {
					throw new ArgumentNullException ("waitHandles");
				}
				throw new ArgumentException ();
			}

#if false
	//
	// Although we should thrown an exception if this is an STA thread,
	// Mono does not know anything about STA threads, and just makes
	// things like Paint.NET not even possible to work.
	//
	// See bug #78455 for the bug this is supposed to fix. 
	// 
			if (waitAll && length > 1 && IsSTAThread)
				throw new NotSupportedException ("WaitAll for multiple handles is not allowed on an STA thread.");
#endif
			/*
			 * FIXME: What happens when another derived class is in the array?
			 */
 
			var waitables = new StWaitable[waitHandles.Length];
			for (int i = 0; i < waitables.Length; ++i) {
				var w = waitHandles [i];

				if (w == null) {
					throw new ArgumentNullException ("waitHandles", "null handle");
				}

				w.CheckCanWait ();
				waitables [i] = waitHandles [i].waitable;
			}
			return waitables;
		}

#if false
	// usage of property is commented - see above
		static bool IsSTAThread {
			get {
				bool isSTA = Thread.CurrentThread.ApartmentState ==
					ApartmentState.STA;

				// FIXME: remove this check after Thread.ApartmentState
				// has been properly implemented.
				if (!isSTA) {
					Assembly asm = Assembly.GetEntryAssembly ();
					if (asm != null)
						isSTA = asm.EntryPoint.GetCustomAttributes (typeof (STAThreadAttribute), false).Length > 0;
				}

				return isSTA;
			}
		}
#endif
		public const int WaitTimeout = 258;

		protected static readonly IntPtr InvalidHandle = new IntPtr (-1);
		
		private static readonly SafeWaitHandle DISPOSED = new SafeWaitHandle (InvalidHandle, false);
		private SafeWaitHandle safe_wait_handle;
		private readonly object locker = new object ();

		internal StWaitable waitable;

		internal WaitHandle (StWaitable waitable) {
			this.waitable = waitable;
		}

		protected WaitHandle () { }

		/*
		 * In 2.0 we use SafeWaitHandles instead of IntPtrs
		 */

		[Obsolete ("In the profiles > 2.x, use SafeHandle instead of Handle")]
		public virtual IntPtr Handle {
			get { return SafeWaitHandle.DangerousGetHandle (); }

			[SecurityPermission (SecurityAction.LinkDemand, UnmanagedCode = true)]
			[SecurityPermission (SecurityAction.InheritanceDemand, UnmanagedCode = true)]
			set {
				SafeWaitHandle = value == InvalidHandle
				               ? new SafeWaitHandle (InvalidHandle, false)
				               : new SafeWaitHandle (value, true);
			}
		}

		/*
		 * When running on Windows, inflates the synchronizer to the corresponding
		 * kernel object; otherwise, requests a handle from the runtime.
		 */

		public SafeWaitHandle SafeWaitHandle {
			[ReliabilityContract (Consistency.WillNotCorruptState, Cer.MayFail)]
			get {
				lock (locker) {
					if (safe_wait_handle != null) {
						return safe_wait_handle;
					}
					
					var w = waitable;
					if (w == null) {
						if (safe_wait_handle == null || safe_wait_handle == DISPOSED) {
							return new SafeWaitHandle (InvalidHandle, false);
						}

						/*
						 * This must be another derived class.
						 */

						return safe_wait_handle;
					}

					RuntimeHelpers.PrepareConstrainedRegions ();
					try { }
					finally {
						if (Environment.OSVersion.Platform == PlatformID.Win32NT) {
							w.Inflate ();
							safe_wait_handle = w.swhandle;
						} else {
							safe_wait_handle = new SafeWaitHandle (StInternalMethods.RegisterHandle_internal (w), true);
						}
					}

					return safe_wait_handle;
				}
			}

			/*
			 * This can recover the object from a call to Dispose.
			 */
 
			[ReliabilityContract (Consistency.WillNotCorruptState, Cer.Success)]
			set {
				lock (locker) {
					if (value == null) {

						RuntimeHelpers.PrepareConstrainedRegions ();
						try { }
						finally {
							waitable = null;
							safe_wait_handle = null;
						}
						return;
					}

					var w = waitable ?? (waitable = CreateWaitable ());
					if (w == null) {

						/*
						 * This is another derived class.
						 */
 
						safe_wait_handle = value;
						return;
					}

					RuntimeHelpers.PrepareConstrainedRegions ();
					try { }
					finally {
						if (Environment.OSVersion.Platform == PlatformID.Win32NT) {
							if (w.swhandle == null) {
								w.Inflate (value);
							}
							safe_wait_handle = value;
						} else {
							var nwaitable = StInternalMethods.ResolveHandle_internal (value.DangerousGetHandle ());
							if (nwaitable == null) {
								safe_wait_handle = value;
								waitable = nwaitable;
							} else {

								/*
								 * The handle is invalid.
								 */

								waitable = null;
								safe_wait_handle = new SafeWaitHandle(InvalidHandle, false);
							}
						}
					}
				}
			}
		}

		internal virtual StWaitable CreateWaitable () {
			return null;
		}

		public static bool WaitAll (WaitHandle[] waitHandles)
		{
			return WaitAll (waitHandles, Timeout.Infinite, false);
		}

		public static bool WaitAll (WaitHandle[] waitHandles, int millisecondsTimeout)
		{
			return WaitAll (waitHandles, millisecondsTimeout, false);
		}

		public static bool WaitAll (WaitHandle[] waitHandles, TimeSpan timeout)
		{
			return WaitAll (waitHandles, timeout, false);
		}

		public static bool WaitAll (WaitHandle[] waitHandles, TimeSpan timeout, bool exitContext)
		{
			long ms = (long)timeout.TotalMilliseconds;
			if (ms > Int32.MaxValue) {
				throw new ArgumentOutOfRangeException ("timeout");
			}

			return WaitAll (waitHandles, (int)ms, exitContext);
		}

		public static bool WaitAll (WaitHandle[] waitHandles, int millisecondsTimeout, bool exitContext)
		{
			if (millisecondsTimeout < Timeout.Infinite) {
				throw new ArgumentOutOfRangeException ("millisecondsTimeout");
			}

			StWaitable[] waitables = CheckArray (waitHandles, true);

			try {
				if (exitContext) {
					SynchronizationAttribute.ExitContext ();
				}

				return StWaitable.WaitAll (waitables, new StCancelArgs (millisecondsTimeout, true));
			} catch (AbandonedMutexException e) {
				e.Mutex = (Mutex) waitHandles [e.MutexIndex];
				throw;
			} finally {
				if (exitContext) {
					SynchronizationAttribute.EnterContext ();
				}
			}
		}

		[ReliabilityContract (Consistency.WillNotCorruptState, Cer.MayFail)]
		public static int WaitAny (WaitHandle[] waitHandles)
		{
			return WaitAny (waitHandles, Timeout.Infinite, false);
		}

		[ReliabilityContract (Consistency.WillNotCorruptState, Cer.MayFail)]
		public static int WaitAny (WaitHandle[] waitHandles, TimeSpan timeout)
		{
			return WaitAny (waitHandles, timeout, false);
		}

		[ReliabilityContract (Consistency.WillNotCorruptState, Cer.MayFail)]
		public static int WaitAny (WaitHandle[] waitHandles, int millisecondsTimeout)
		{
			return WaitAny (waitHandles, millisecondsTimeout, false);
		}

		[ReliabilityContract (Consistency.WillNotCorruptState, Cer.MayFail)]
		public static int WaitAny (WaitHandle[] waitHandles, TimeSpan timeout, bool exitContext)
		{
			long ms = (long)timeout.TotalMilliseconds;
			if (ms > Int32.MaxValue) {
				throw new ArgumentOutOfRangeException ("timeout");
			}

			return WaitAny (waitHandles, (int)ms, exitContext);
		}

		[ReliabilityContract (Consistency.WillNotCorruptState, Cer.MayFail)]
		public static int WaitAny (WaitHandle[] waitHandles, int millisecondsTimeout, bool exitContext)
		{
			if (millisecondsTimeout < Timeout.Infinite) {
				throw new ArgumentOutOfRangeException ("millisecondsTimeout");
			}

			StWaitable[] waitables = CheckArray (waitHandles, false);
			
			try {
				if (exitContext) {
					SynchronizationAttribute.ExitContext ();
				}

				int ws = StWaitable.WaitAny (waitables, new StCancelArgs (millisecondsTimeout, true));
				return ws == StParkStatus.Timeout ? WaitTimeout : ws;
			} catch (AbandonedMutexException e) {
				e.Mutex = (Mutex) waitHandles [e.MutexIndex];
				throw;
			} finally {
				if (exitContext) {
					SynchronizationAttribute.EnterContext ();
				}
			}
		}

		public static bool SignalAndWait (WaitHandle toSignal, WaitHandle toWaitOn)
		{
			return SignalAndWait (toSignal, toWaitOn, -1, false);
		}

		public static bool SignalAndWait (WaitHandle toSignal, WaitHandle toWaitOn,
		                                  TimeSpan timeout, bool exitContext)
		{
			long ms = (long)timeout.TotalMilliseconds;
			if (ms > Int32.MaxValue) {
				throw new ArgumentOutOfRangeException ("timeout");
			}

			return SignalAndWait (toSignal, toWaitOn, (int)ms, false);
		}

		public static bool SignalAndWait (WaitHandle toSignal, WaitHandle toWaitOn,
		                                  int millisecondsTimeout, bool exitContext)
		{
			if (toSignal == null) {
				throw new ArgumentNullException ("toSignal");
			}

			if (toWaitOn == null) {
				throw new ArgumentNullException ("toWaitOn");
			}

			if (millisecondsTimeout < Timeout.Infinite) {
				throw new ArgumentOutOfRangeException ("millisecondsTimeout");
			}

			toSignal.CheckDisposed ();
			toWaitOn.CheckDisposed ();

			var tos = toSignal.waitable;
			var tow = toWaitOn.waitable;

			if (tos == null || tow == null) {
				return false;
			}

			try {
				if (exitContext) {
					SynchronizationAttribute.ExitContext ();
				}

				return StWaitable.SignalAndWait (tos, tow, new StCancelArgs (millisecondsTimeout, true));
			} finally {
				if (exitContext) {
					SynchronizationAttribute.EnterContext ();
				}
			}
		}

		public virtual bool WaitOne ()
		{
			return WaitOne (Timeout.Infinite, false);
		}

		public virtual bool WaitOne (int millisecondsTimeout)
		{
			return WaitOne (millisecondsTimeout, false);
		}

		public virtual bool WaitOne (TimeSpan timeout)
		{
			return WaitOne (timeout, false);
		}

		public virtual bool WaitOne (TimeSpan timeout, bool exitContext)
		{
			long ms = (long)timeout.TotalMilliseconds;
			if (ms > Int32.MaxValue) {
				throw new ArgumentOutOfRangeException ("timeout");
			}

			return WaitOne ((int)ms, exitContext);
		}

		public virtual bool WaitOne (int millisecondsTimeout, bool exitContext)
		{
			CheckCanWait ();

			if (waitable == null) {
				/*
				 * FIXME: What about a derived class?
				 */
 
				return false;
			}

			if (millisecondsTimeout < Timeout.Infinite) {
				throw new ArgumentOutOfRangeException ("millisecondsTimeout");
			}

			try {
				if (exitContext) {
					SynchronizationAttribute.ExitContext ();
				}

				return waitable.TryWaitOne (new StCancelArgs (millisecondsTimeout, true));
			} catch (AbandonedMutexException e) {
				e.Mutex = this as Mutex;
				throw;
			} finally {
				if (exitContext) {
					SynchronizationAttribute.EnterContext ();
				}
			}
		}

		internal void Signal ()
		{
			var w = waitable;
			if (w != null && !w._Release()) {
				throw w._SignalException;
			}
		}

		public virtual void Close ()
		{
			Dispose (true);
			GC.SuppressFinalize (this);
		}

#if NET_4_0 || MOBILE || MOONLIGHT
		public void Dispose ()
#else		
		void IDisposable.Dispose ()
#endif
		{
			Close ();
		}

		protected virtual void Dispose (bool explicitDisposing)
		{
			if (explicitDisposing && safe_wait_handle != DISPOSED) {
				lock (locker) {
					var swhandle = safe_wait_handle;
					safe_wait_handle = DISPOSED;

					if (swhandle != null && swhandle != DISPOSED) {
						swhandle.Close ();
					}

					if (waitable != null) {
						waitable = null;
					}
				}
			}
		}

		internal void CheckCanWait ()
		{
			CheckDisposed ();

			if (RemotingServices.IsTransparentProxy (this)) {
				throw new InvalidOperationException ("Cannot wait for a transparent proxy for a WaitHandle in another application domain");
			}
		}

		internal void CheckDisposed ()
		{
			if (safe_wait_handle == DISPOSED || (waitable == null && safe_wait_handle == null)) {
				throw new ObjectDisposedException (GetType ().FullName);
			}
		}
	}
}