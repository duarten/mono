//
// System.Threading.SafeWaitHandle.cs
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

using System;
using System.Runtime.ConstrainedExecution;
using System.Runtime.InteropServices;

namespace Microsoft.Win32.SafeHandles {

	public sealed class SafeWaitHandle : SafeHandleZeroOrMinusOneIsInvalid {

		[ReliabilityContract (Consistency.WillNotCorruptState, Cer.MayFail)]
		public SafeWaitHandle (IntPtr existingHandle, bool ownsHandle) 
            : base (ownsHandle)
		{
			SetHandle (existingHandle);
		}

		protected override bool ReleaseHandle ()
		{
            if (handle != IntPtr.Zero) {
                GCHandle.FromIntPtr (handle).Free ();
            }
		    return true;
		}
	}
}