//
// System.Threading.StMutex.cs
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

using Microsoft.Win32.SafeHandles;

namespace System.Threading
{
	internal class StInflatedMutex : StWaitable
	{
		internal StInflatedMutex (bool initalOwner)
		{
			swhandle = new SafeWaitHandle (StInternalMethods.CreateMutex (IntPtr.Zero, initalOwner, null), true);
		}

		internal override bool _AllowsAcquire {
			get { return false; }
		}

		internal void Exit() {
			if (!_Release()) {
				throw _SignalException;
			}
		}
		
		internal override bool _Release()
		{
			bool release = false;
			try {
				swhandle.DangerousAddRef (ref release);
				return StInternalMethods.ReleaseMutex (swhandle.DangerousGetHandle ());
			} finally {
				if (release) {
					swhandle.DangerousRelease ();
				}
			}
		}

		internal override bool _TryAcquire ()
		{
			return false;
		}

		internal override StWaitBlock _WaitAnyPrologue (StParker pk, int key, ref StWaitBlock hint,
		                                                ref int sc)
		{
			hint = INFLATED;
			return null;
		}

		internal override StWaitBlock _WaitAllPrologue (StParker pk, ref StWaitBlock hint, ref int sc)
		{
			hint = INFLATED;
			return null;
		}

		internal override void _CancelAcquire (StWaitBlock wb, StWaitBlock hint)
		{ }

		internal override Exception _SignalException {
			get { return new ApplicationException ("The calling thread does not own the mutex."); }
		}
	}
}