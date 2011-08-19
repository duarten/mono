//
// System.Threading.Mutex.cs
//
// Author:
//   Dick Porter (dick@ximian.com)
//   Veronica De Santis (veron78@interfree.it)
//	  Duarte Nunes (duarte.m.nunes@gmail.com)
//
// (C) Ximian, Inc.  http://www.ximian.com
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

using System.Runtime.ConstrainedExecution;
using System.Runtime.InteropServices;
#if !NET_2_1
using System.Security.AccessControl;
#endif
using System.Security.Permissions;

namespace System.Threading
{
	[ComVisible (true)]
	public sealed class Mutex : WaitHandle 
	{
		private const int defaultSpinCount = 256;

		[ReliabilityContractAttribute (Consistency.WillNotCorruptState, Cer.MayFail)]
		public Mutex ()
            : this (false) { }
		
		[ReliabilityContractAttribute (Consistency.WillNotCorruptState, Cer.MayFail)]
		public Mutex (bool initiallyOwned)
			: base (Environment.OSVersion.Platform == PlatformID.Win32NT 
			        ? (StWaitable) new StInflatedMutex (initiallyOwned) 
			        : new StMutex (initiallyOwned, defaultSpinCount))
      { }

		[ReliabilityContractAttribute (Consistency.WillNotCorruptState, Cer.MayFail)]
		[SecurityPermission (SecurityAction.LinkDemand, UnmanagedCode = true)]
		public Mutex (bool initiallyOwned, string name)
		{
         throw new NotImplementedException ();
		}

		[ReliabilityContractAttribute (Consistency.WillNotCorruptState, Cer.MayFail)]
		[SecurityPermission (SecurityAction.LinkDemand, UnmanagedCode = true)]
		public Mutex (bool initiallyOwned, string name, out bool createdNew)
		{
			throw new NotImplementedException ();
		}

#if !NET_2_1
		[MonoTODO ("Implement MutexSecurity")]
		[ReliabilityContractAttribute (Consistency.WillNotCorruptState, Cer.MayFail)]
		public Mutex (bool initiallyOwned, string name, out bool createdNew, MutexSecurity mutexSecurity)
		{
			throw new NotImplementedException ();
		}

		public MutexSecurity GetAccessControl ()
		{
			throw new NotImplementedException ();
		}

		public static Mutex OpenExisting (string name)
		{
			return OpenExisting (name, MutexRights.Synchronize | MutexRights.Modify);
		}
		
		public static Mutex OpenExisting (string name, MutexRights rights)
		{
         throw new NotImplementedException ();
		}
#endif

		[ReliabilityContractAttribute (Consistency.WillNotCorruptState, Cer.MayFail)]
		public void ReleaseMutex ()
		{
			if (Environment.OSVersion.Platform == PlatformID.Win32NT) {
				var mutex = waitable as StInflatedMutex;
				if (mutex != null) {
					mutex.Exit ();
				}
			} else {
				var mutex = waitable as StMutex;
				if (mutex != null) {
					mutex.Exit ();
				}		
			}
		}

		internal override StWaitable CreateWaitable ()
		{
			return Environment.OSVersion.Platform == PlatformID.Win32NT 
			    ? (StWaitable) new StInflatedMutex (false) 
			    : new StMutex (defaultSpinCount);
		}

#if !NET_2_1
		public void SetAccessControl (MutexSecurity mutexSecurity)
		{
			throw new NotImplementedException ();
		}
#endif
	}
}
