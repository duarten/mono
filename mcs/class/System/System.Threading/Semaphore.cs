//
// System.Threading.Semaphore.cs
//
// Author:
//	Sebastien Pouliot  <sebastien@ximian.com>
//
// Copyright (C) 2005 Novell, Inc (http://www.novell.com)
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

#if NET_2_0

using System.Runtime.ConstrainedExecution;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Runtime.CompilerServices;
using System.IO;

namespace System.Threading 
{
	[ComVisible (false)]
	public sealed class Semaphore : WaitHandle 
	{
		private const int defaultSpinCount = 256;
		
		public Semaphore (int initialCount, int maximumCount)
			: base (
			new StSemaphore (initialCount, maximumCount) 
#if !NET_4_0
			{ _SignalException = () => { throw new SemaphoreFullException (); } }
#endif
			)
		{ }

		public Semaphore (int initialCount, int maximumCount, string name)
		{
			if (initialCount < 0)
				throw new ArgumentOutOfRangeException ("initialCount", "< 0");
			if (maximumCount < 1)
				throw new ArgumentOutOfRangeException ("maximumCount", "< 1");
			if (initialCount > maximumCount)
				throw new ArgumentException ("initialCount > maximumCount");

			throw new NotImplementedException ();
		}

		public Semaphore (int initialCount, int maximumCount, string name, out bool createdNew)
			: this (initialCount, maximumCount, name, out createdNew, null)
		{ 		}

		[MonoTODO ("Does not support access control, semaphoreSecurity is ignored")]
		public Semaphore (int initialCount, int maximumCount, string name, out bool createdNew, 
			SemaphoreSecurity semaphoreSecurity)
		{
			if (initialCount < 0)
				throw new ArgumentOutOfRangeException ("initialCount", "< 0");
			if (maximumCount < 1)
				throw new ArgumentOutOfRangeException ("maximumCount", "< 1");
			if (initialCount > maximumCount)
				throw new ArgumentException ("initialCount > maximumCount");

			throw new NotImplementedException ();
		}

		[MonoTODO]
		public SemaphoreSecurity GetAccessControl ()
		{
			throw new NotImplementedException ();
		}

		[PrePrepareMethod]
		[ReliabilityContract (Consistency.WillNotCorruptState, Cer.Success)]
		public int Release ()
		{
			return Release (1);
		}

		[ReliabilityContract (Consistency.WillNotCorruptState, Cer.Success)]
		public int Release (int releaseCount)
		{
			if (releaseCount < 1)
				throw new ArgumentOutOfRangeException ("releaseCount");

			var sem = Waitable as StSemaphore;
			return sem != null ? sem.Release(releaseCount) : 0; 
			return 0;
		}

		internal override StWaitable CreateWaitable () 
		{
			return new StSemaphore (initialCount, maximumCount) 
#if !NET_4_0
			{ _SignalException = () => { throw new SemaphoreFullException (); } }
#endif
			;
		}

		[MonoTODO]
		public void SetAccessControl (SemaphoreSecurity semaphoreSecurity)
		{
			if (semaphoreSecurity == null)
				throw new ArgumentNullException ("semaphoreSecurity");

			throw new NotImplementedException ();
		}

		// static methods

		public static Semaphore OpenExisting (string name)
		{
			return OpenExisting (name, SemaphoreRights.Synchronize | SemaphoreRights.Modify);
		}

		public static Semaphore OpenExisting (string name, SemaphoreRights rights)
		{
			if (name == null)
				throw new ArgumentNullException ("name");
			if ((name.Length ==0) || (name.Length > 260))
				throw new ArgumentException ("name", Locale.GetText ("Invalid length [1-260]."));

			throw new NotImplementedException ();

			/*
			MonoIOError error;
			IntPtr handle = OpenSemaphore_internal (name, rights,
								out error);
			if (handle == (IntPtr)null) {
				if (error == MonoIOError.ERROR_FILE_NOT_FOUND) {
					throw new WaitHandleCannotBeOpenedException (Locale.GetText ("Named Semaphore handle does not exist: ") + name);
				} else if (error == MonoIOError.ERROR_ACCESS_DENIED) {
					throw new UnauthorizedAccessException ();
				} else {
					throw new IOException (Locale.GetText ("Win32 IO error: ") + error.ToString ());
				}
			}
			
			return new Semaphore (handle);
			*/
		}
	}
}

#endif
