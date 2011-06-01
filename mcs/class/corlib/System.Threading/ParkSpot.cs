//
// System.Threading.ParkSpot.cs
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

namespace System.Threading 
{

	internal struct ParkSpot 
	{
        private IntPtr ps;

        internal void Alloc () 
				{
            ps = Alloc_internal ();
        }

        internal void Free () 
				{
            Free_internal (ps);
        }

        internal void Set () 
				{
            Set_internal (ps);
        }

        internal void Wait (StParker pk, StCancelArgs cargs) 
				{
            int ws;
            bool interrupted = false;
            do {
                try {
                    ws = Wait_internal (ps, cargs.Timeout) 
											 ? StParkStatus.Success
                       : StParkStatus.Timeout;
                    break;
                } catch (ThreadInterruptedException) {
                    if (cargs.Interruptible) {
                        ws = StParkStatus.Interrupted;
                        break;
                    }
										interrupted = true;
                }
            } while (true);

            //
            // If the wait was cancelled due to an internal canceller, try
            // to cancel the park operation. If we fail, wait unconditionally
            // until the park spot is signalled.
            //

            if (ws != StParkStatus.Success) {
                if (pk.TryCancel()) {
                    pk.UnparkSelf(ws);
                } else {
										if (ws == StParkStatus.Interrupted)  {
										    interrupted = true;
										}
										
                    do {
                        try {
                            Wait_internal (ps, Timeout.Infinite);
                            break;
                        } catch (ThreadInterruptedException) {
                            interrupted = true;
                        }
                    } while (true);
                }
            }

            //
            // If we were interrupted but can't return the *interrupted*
            // wait status, reassert the interrupt on the current thread.
            //

            if (interrupted) {
                Thread.CurrentThread.Interrupt ();
            }
        }

        [MethodImplAttribute(MethodImplOptions.InternalCall)]
				private extern static IntPtr Alloc_internal ();
        
        [MethodImplAttribute(MethodImplOptions.InternalCall)]
				private extern static void Free_internal (IntPtr ps);

        [MethodImplAttribute(MethodImplOptions.InternalCall)]
				private extern static void Set_internal (IntPtr ps);

        [MethodImplAttribute(MethodImplOptions.InternalCall)]
				private extern static bool Wait_internal (IntPtr ps, int timeout);
    }
}
