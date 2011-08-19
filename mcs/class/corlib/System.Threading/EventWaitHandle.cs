//
// System.Threading.EventWaitHandle.cs
//
// Author:
// 	Dick Porter (dick@ximian.com)
//		Duarte Nunes (duarte.m.nunes@gmail.com)
//
// (C) Ximian, Inc.	(http://www.ximian.com)
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

using System.Runtime.InteropServices;
#if !NET_2_1
using System.Security.AccessControl;
#endif

namespace System.Threading
{
	[ComVisible (true)]
	public class EventWaitHandle : WaitHandle
	{
		private static bool IsManualReset (EventResetMode mode)
		{
			if ((mode < EventResetMode.AutoReset) || (mode > EventResetMode.ManualReset)) {
				throw new ArgumentException ("mode");
			}
			return mode == EventResetMode.ManualReset;
		}
		
      internal EventWaitHandle (StWaitable waitable)
			: base (waitable)
      { }

		public EventWaitHandle (bool initialState, EventResetMode mode)
			: base (IsManualReset (mode) ? (StWaitable) new StNotificationEvent (initialState) 
												  : new StSynchronizationEvent (initialState))
		{ }
		
		public EventWaitHandle (bool initialState, EventResetMode mode, string name)
		{
         throw new NotImplementedException ();
		}
		
		public EventWaitHandle (bool initialState, EventResetMode mode,
										string name, out bool createdNew)
		{
         throw new NotImplementedException ();
		}

#if !NET_2_1
		[MonoTODO ("Implement access control")]
		public EventWaitHandle (bool initialState, EventResetMode mode,
										string name, out bool createdNew,
										EventWaitHandleSecurity eventSecurity)
		{
         throw new NotImplementedException ();
		}
		
		[MonoTODO]
		public EventWaitHandleSecurity GetAccessControl ()
		{
			throw new NotImplementedException ();
		}

		public static EventWaitHandle OpenExisting (string name)
		{
         throw new NotImplementedException ();
		}

		public static EventWaitHandle OpenExisting (string name, EventWaitHandleRights rights)
		{
         throw new NotImplementedException ();
		}
#endif
		public bool Reset ()
		{
         if (waitable is StSynchronizationEvent) {
            return ((StSynchronizationEvent)waitable).Reset ();
         }
            
         if (waitable is StNotificationEvent) {
            return ((StNotificationEvent)waitable).Reset ();
         }

		   return false;
		}
		
		public bool Set ()
		{
         if (waitable is StSynchronizationEvent) {
				return ((StSynchronizationEvent)waitable).Set ();
			}
			
			if (waitable is StNotificationEvent) {
				return ((StNotificationEvent)waitable).Set ();
			}

			return false;
		}

#if !NET_2_1
		[MonoTODO]
		public void SetAccessControl (EventWaitHandleSecurity eventSecurity)
		{
			throw new NotImplementedException ();
		}
#endif
	}
}
