//
// System.Threading.EventWaitHandle.cs
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
			if ((mode < EventResetMode.AutoReset) || (mode > EventResetMode.ManualReset))
				throw new ArgumentException ("mode");
			return (mode == EventResetMode.ManualReset);
		}
		
        internal EventWaitHandle (StWaitable waitable)
        {
            Waitable = waitable;
        }

		public EventWaitHandle (bool initialState, EventResetMode mode)
		{
            Waitable = IsManualReset (mode)
                     ? (StWaitable) new StNotificationEvent(initialState) 
                     : new StSynchronizationEvent(initialState);
		}
		
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
            if (Waitable is StSynchronizationEvent) {
                return ((StSynchronizationEvent)Waitable).Reset ();
            }
            
            if (Waitable is StNotificationEvent) {
                return ((StNotificationEvent)Waitable).Reset();
            }

		    return false;
		}
		
		public bool Set ()
        {
            if (Waitable is StSynchronizationEvent) {
                return ((StSynchronizationEvent)Waitable).Set ();
            }
            
            if (Waitable is StNotificationEvent) {
                return ((StNotificationEvent)Waitable).Set();
            }

		    return false;
		}

        internal override bool _WaitOne(int timeout)
        {
            if (Waitable is StSynchronizationEvent) {
                return ((StSynchronizationEvent)Waitable).Wait (new StCancelArgs (timeout));
            }
            
            if (Waitable is StNotificationEvent) {
                return ((StNotificationEvent)Waitable).Wait (new StCancelArgs (timeout));
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
