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

#if NET_4_0 || MOBILE
      internal CancellationToken CancellationToken  { get; private set; }
#endif

      internal bool Interruptible { get; private set; }

#if NET_4_0 || MOBILE
      internal static readonly StCancelArgs None = new StCancelArgs (-1, default (CancellationToken), false);

		internal StCancelArgs (int timeout, CancellationToken token, bool interruptible) 
		{
         if (timeout < -1) {
            throw new ArgumentOutOfRangeException ("timeout", timeout, "Wrong timeout value");
         }

         this = new StCancelArgs ();
         Timeout = timeout;
         CancellationToken = token;
         Interruptible = interruptible;
      }

      internal StCancelArgs (int timeout) 
			: this (timeout, default (CancellationToken), false) { }

      internal StCancelArgs (TimeSpan timeout) 
			: this (timeout.Milliseconds, default (CancellationToken), false) { }

      internal StCancelArgs (CancellationToken token) 
			: this (-1, token, false) { }

      internal StCancelArgs (bool interruptible) 
			: this (-1, default (CancellationToken), interruptible) { }
        
      internal StCancelArgs (int timeout, bool interruptible) 
			: this (timeout, default (CancellationToken), interruptible) { }

      internal StCancelArgs (TimeSpan timeout, bool interruptible) 
			: this (timeout.Milliseconds, default (CancellationToken), interruptible) { }
 
      internal StCancelArgs (int timeout, CancellationToken token) 
			: this (timeout, token, false) { }

      internal StCancelArgs (TimeSpan timeout, CancellationToken token) 
			: this (timeout.Milliseconds, token, false) { }

      internal StCancelArgs (CancellationToken token, bool interruptible) 
			: this (-1, token, interruptible) { }
#else
		internal static readonly StCancelArgs None = new StCancelArgs (-1, false);

		internal StCancelArgs (int timeout, bool interruptible) 
		{
         if (timeout < -1) {
            throw new ArgumentOutOfRangeException ("timeout", timeout, "Wrong timeout value");
         }

         this = new StCancelArgs ();
         Timeout = timeout;
         Interruptible = interruptible;
      }

		internal StCancelArgs (bool interruptible) 
			: this (-1, interruptible) { }
        
      internal StCancelArgs (int timeout) 
			: this (timeout, true) { }
#endif

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

      internal void ThrowIfException (int ws) 
		{
         switch (ws) {
#if NET_4_0 || MOBILE
				case StParkStatus.Cancelled: throw new OperationCanceledException (CancellationToken);
#endif
            case StParkStatus.Interrupted: throw new ThreadInterruptedException ();
            default: return;
         }
      }
    }
}