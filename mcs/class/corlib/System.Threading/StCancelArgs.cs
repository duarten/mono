//
// System.Threading.CancelArgs.cs
//
// Copyright 2011 Carlos Martins, Duarte Nunes
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

namespace System.Threading 
{
    internal struct StCancelArgs 
	{
        internal int Timeout { get; private set; }

        internal StAlerter Alerter { get; private set; }

        internal bool Interruptible { get; private set; }

        internal static readonly StCancelArgs None = new StCancelArgs(-1, null, false);

        internal StCancelArgs (int timeout, StAlerter alerter, bool interruptible) 
		{
            if (timeout < -1) {
                throw new ArgumentOutOfRangeException ("timeout", timeout, "Wrong timeout value");
            }

            this = new StCancelArgs ();
            Timeout = timeout;
            Alerter = alerter;
            Interruptible = interruptible;
        }

        internal StCancelArgs (int timeout) 
			: this (timeout, null, false) { }

        internal StCancelArgs (TimeSpan timeout) 
			: this (timeout.Milliseconds, null, false) { }

        internal StCancelArgs (StAlerter alerter) 
			: this (-1, alerter, false) { }

        internal StCancelArgs (bool interruptible) 
			: this (-1, null, interruptible) { }
        
        internal StCancelArgs (int timeout, bool interruptible) 
			: this (timeout, null, interruptible) { }

        internal StCancelArgs (TimeSpan timeout, bool interruptible) 
			: this (timeout.Milliseconds, null, interruptible) { }
 
        internal StCancelArgs (int timeout, StAlerter alerter) 
			: this (timeout, alerter, false) { }

        internal StCancelArgs (TimeSpan timeout, StAlerter alerter) 
			: this (timeout.Milliseconds, alerter, false) { }

        internal StCancelArgs (StAlerter alerter, bool interruptible) 
			: this (-1, alerter, interruptible) { }

        //
        // Adjusts the timeout value, returning false if the timeout 
        // has expired.
        //

        internal bool AdjustTimeout (ref int lastTime) 
		{
            if (Timeout == Threading.Timeout.Infinite) {
                return true;
            }
            
            int now = Environment.TickCount;
            int e = now == lastTime ? 1 : now - lastTime;
            if (Timeout <= e) {
                return false;
            }

            Timeout -= e;
            lastTime = now;
            return true;
        }

        //
        // Thows the cancellation exception, if appropriate;
        // otherwise, does noting.
        //

        internal static void ThrowIfException (int ws) 
		{
            switch (ws) {
                case StParkStatus.Alerted: throw new StThreadAlertedException ();
                case StParkStatus.Interrupted: throw new ThreadInterruptedException ();
                default: return;
            }
        }
    }
}