//
// System.Threading.Mutex.cs
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
		[ReliabilityContractAttribute (Consistency.WillNotCorruptState, Cer.MayFail)]
		public Mutex ()
            : this(false) { }
		
		[ReliabilityContractAttribute (Consistency.WillNotCorruptState, Cer.MayFail)]
		public Mutex (bool initiallyOwned) 
        {
            Waitable = new StReentrantFairLock(initiallyOwned);
		}

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

        internal override bool _WaitOne (int timeout)
        {
            var flock = Waitable as StReentrantFairLock;
            return flock != null ? flock.Enter(new StCancelArgs (timeout)) : false;
        }

		[ReliabilityContractAttribute (Consistency.WillNotCorruptState, Cer.MayFail)]
		public void ReleaseMutex ()
        {
            var flock = Waitable as StReentrantFairLock;
            if (flock == null) {
                return;
            }

            try { 
                flock.Exit();
            } catch (InvalidOperationException) {
                throw new ApplicationException("Mutex is not owned");
            }
		}

#if !NET_2_1
		public void SetAccessControl (MutexSecurity mutexSecurity)
		{
			throw new NotImplementedException ();
		}
#endif
	}
}
