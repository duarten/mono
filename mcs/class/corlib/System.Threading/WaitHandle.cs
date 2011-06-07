//
// System.Threading.WaitHandle.cs
//
// Copyright 2011 Duarte Nunes
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
		static StWaitable[] CheckArray (WaitHandle [] waitHandles, bool waitAll)
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
			foreach (WaitHandle w in waitHandles) {
                if (w == null) {
                    throw new ArgumentNullException ("waitHandles", "null handle");
                }

                if (w.Waitable == null) {
                    throw new ArgumentException ("null element found", "waitHandles");
                }
			}

            var waitables = new StWaitable[waitHandles.Length];
            for (int i = 0; i < waitables.Length; ++i) {
                waitables[i] = waitHandles[i].Waitable;
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

        protected static readonly IntPtr InvalidHandle = (IntPtr) (-1);
        
		bool disposed;

		//
		// In 2.0 we use SafeWaitHandles instead of IntPtrs
		//
		
        private SafeWaitHandle handle;
	    private readonly StFairLock handleLock = new StFairLock();

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

        public SafeWaitHandle SafeWaitHandle {
			[ReliabilityContract (Consistency.WillNotCorruptState, Cer.MayFail)]
			get {
			    handleLock.Enter ();
                try {
                    SafeWaitHandle swh;
                    if ((swh = handle) == null) {
                        IntPtr h = GCHandle.Alloc (Waitable, GCHandleType.Pinned).AddrOfPinnedObject ();
                        handle = swh = new SafeWaitHandle (h, true);
                    }
                    return swh;
                } finally {
			        handleLock.Exit();
			    }
			}

			[ReliabilityContract (Consistency.WillNotCorruptState, Cer.Success)]
			set {
			    SafeWaitHandle owh = null;
                handleLock.Enter ();
                try {
                    owh = handle;
                    IntPtr h = (handle = value).DangerousGetHandle ();

                    Waitable = h == InvalidHandle 
                             ? null
                             : GCHandle.FromIntPtr (h).Target as StWaitable;
                } finally {
			        handleLock.Exit();
                    if (owh != null) {
                        owh.Dispose ();
                    }
			    }
			}
		}

        internal StWaitable Waitable { get; set; }

		public static bool WaitAll (WaitHandle[] waitHandles)
		{
			return WaitAll(waitHandles, Timeout.Infinite, false);
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
			double ms = timeout.TotalMilliseconds;
            if (ms > Int32.MaxValue) {
                throw new ArgumentOutOfRangeException ("timeout");
            }

            return WaitAll (waitHandles,  Convert.ToInt32 (ms), exitContext);
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

		        return StWaitable.WaitAll (waitables, new StCancelArgs (millisecondsTimeout));
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
			double ms = timeout.TotalMilliseconds;
            if (ms > Int32.MaxValue) {
                throw new ArgumentOutOfRangeException ("timeout");
            }

		    return WaitAny (waitHandles,  Convert.ToInt32 (ms), exitContext);
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

		        int ws = StWaitable.WaitAny (waitables, new StCancelArgs (millisecondsTimeout));
		        return ws == StParkStatus.Timeout ? WaitTimeout : ws;

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
			double ms = timeout.TotalMilliseconds;
            if (ms > Int32.MaxValue) {
                throw new ArgumentOutOfRangeException ("timeout");
            }

		    return SignalAndWait (toSignal, toWaitOn, Convert.ToInt32 (ms), false);
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

            try {
				if (exitContext) {
				    SynchronizationAttribute.ExitContext ();
				}

		        return StWaitable.SignalAndWait (toSignal.Waitable, toWaitOn.Waitable, 
                                                 new StCancelArgs (millisecondsTimeout));
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
            double ms = timeout.TotalMilliseconds;
            if (ms > Int32.MaxValue) {
                throw new ArgumentOutOfRangeException ("timeout");
            }

		    return WaitOne (Convert.ToInt32 (ms), exitContext);
		}

		public virtual bool WaitOne (int millisecondsTimeout, bool exitContext)
		{
			CheckDisposed ();

            if (millisecondsTimeout < Timeout.Infinite) {
                throw new ArgumentOutOfRangeException ("millisecondsTimeout");
            }

			try {
                if (exitContext) {
                    SynchronizationAttribute.ExitContext ();
                }

			    return _WaitOne (millisecondsTimeout);
			} finally {
                if (exitContext) {
                    SynchronizationAttribute.EnterContext ();
                }
			}
		}

	    internal virtual bool _WaitOne (int timeout)
	    {
	        return false;
	    }

		public virtual void Close() {
			Dispose(true);
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
		    if (!disposed) {
		        disposed = true;

		        if (handle != null) {
		            handle.Dispose ();
		        }
		    }
		}

	    internal void CheckDisposed ()
		{
            if (disposed) {
                throw new ObjectDisposedException (GetType ().FullName);
            }
		}

		~WaitHandle() {
			Dispose(false);
		}
	}
}
