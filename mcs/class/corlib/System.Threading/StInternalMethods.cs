//
// System.Threading.StInternalMethods.cs
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

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace System.Threading 
{
	internal static class StInternalMethods 
	{
		/*
		 * Native methods.
       */

		[DllImport ("kernel32.dll")]
		internal static extern IntPtr CreateMutex (IntPtr lpMutexAttributes, bool bInitialOwner,
		                                           string lpName);

		[DllImport ("kernel32.dll")]
		internal static extern bool ReleaseMutex (IntPtr hMutex);

		[DllImport ("kernel32.dll")]
		internal static extern IntPtr CreateEvent (IntPtr lpEventAttributes, bool bManualReset,
		                                           bool bInitialState, string lpName);

		[DllImport ("kernel32.dll")]
		internal static extern bool SetEvent (IntPtr hEvent);
		
		[DllImport ("kernel32.dll")]
		internal static extern bool ResetEvent (IntPtr hEvent);

		[DllImport ("kernel32.dll")]
		internal static extern IntPtr CreateSemaphore (IntPtr lpSemaphoreAttributes, int lInitialCount,
																	  int lMaximumCount, string lpName);

		[DllImport ("kernel32.dll")]
		internal static extern bool ReleaseSemaphore (IntPtr hSemaphore, int lReleaseCount, 
																	 out int lpPreviousCount);

		[DllImport ("kernel32.dll")]
		internal static extern bool CloseHandle (IntPtr handle);

		/*
       * Internal methods.
       */

		[MethodImplAttribute (MethodImplOptions.InternalCall)]
		internal static extern IntPtr RegisterHandle_internal (StWaitable waitble);

		[MethodImplAttribute (MethodImplOptions.InternalCall)]
		internal static extern StWaitable ResolveHandle_internal (IntPtr handle);

		[MethodImplAttribute (MethodImplOptions.InternalCall)]
		internal static extern bool RemoveHandle_internal (IntPtr handle);

		[MethodImplAttribute (MethodImplOptions.InternalCall)]
		internal static extern void Alloc_internal (ref IntPtr ps);
        
		[MethodImplAttribute (MethodImplOptions.InternalCall)]
		internal static extern void Free_internal (IntPtr ps);

		[MethodImplAttribute (MethodImplOptions.InternalCall)]
		internal static extern void Set_internal (IntPtr ps);

		[MethodImplAttribute (MethodImplOptions.InternalCall)]
		internal static extern bool WaitForParkSpot_internal (IntPtr ps, int timeout);

		[MethodImplAttribute (MethodImplOptions.InternalCall)]
		internal static extern bool Wait_internal (IntPtr handle, int timeout);

		[MethodImplAttribute (MethodImplOptions.InternalCall)]
		internal static extern int WaitMultiple_internal (IntPtr ps, SafeWaitHandle[] safe_handles, bool waitAll, int timeout);
	}
}