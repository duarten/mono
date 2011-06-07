//
// System.Threading.StAlerter.cs
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

#pragma warning disable 0420

namespace System.Threading 
{
    internal class StThreadAlertedException : SystemException 
	{
        internal StThreadAlertedException() 
			: base ("Wait alerted") { }

        internal StThreadAlertedException(string msg) 
			: base (msg) { }

        internal StThreadAlertedException(string msg, Exception innerEx) 
			: base (msg, innerEx) {}
    }

    internal class StAlerter
    {
        public static readonly Action<object> CancellationTokenCallback = obj =>
        {
            var alerter = obj as StAlerter;
            if (alerter != null) {
                alerter.Set ();
            }
        };

        //
        // The state of the alerter is a non-blocking stack that links
        // the parkers registered with the alerter. When the alerter
        // is set, its state holds the ALERTED reference.
        //

        internal static StParker ALERTED = new StParker ();
        internal volatile StParker state;

        public StAlerter () 
		{ }

        internal StAlerter (bool alerted) 
		{
            state = alerted ? ALERTED : null;
        }

        //  
        // Returns true if the alerter is set.
        //

        internal bool IsSet
        {
            get { return state == ALERTED; }
        }

        //
        // Registers the specified parker with the alerter.
        //

        internal bool RegisterParker (StParker pk) 
		{
            do {
                StParker s;

                //
                // If the alerter is already set, return false.
                //

                if ((s = state) == ALERTED) {
                    return false;
                }

                //
                // Try to insert the parker in the alert list.
                //

                pk.pnext = s;
                if (Interlocked.CompareExchange (ref state, pk, s) == s) {
                    return true;
                }
            } while (true);
        }

        //
        // Deregisters the specified parker when it isn't the only
        // parker in the alerter list.
        //

        private void SlowDeregisterParker (StParker pk) 
		{
            
            //
            // Absorb the locked parkers at top of the stack.
            //

            StParker p;
            do {
                if ((p = state) == null || p == ALERTED) {
                    return;
                }
                if (p.IsLocked) {
                    Interlocked.CompareExchange(ref state, p.pnext, p);
                } else {
                    break;
                }
            } while (true);

            //
            // Compute a entry ahead the parker that we want to unlink,
            // and try to unsplice it.
            //

            StParker past;
            if ((past = pk.pnext) != null && past.IsLocked) {
                past = past.pnext;
            }

            while (p != null && p != past) {
                StParker n = p.pnext;
                if (n != null && n.IsLocked) {
                    p.CasNext (n, n.pnext);
                } else {
                    p = n;
                }
            }         
        }

        //
        // Deregisters the specified parker from the alerter.
        //

        internal void DeregisterParker (StParker pk) 
		{

            //
            // Very often there is only a parker inserted in the alerter
            // list. So, consider first this case.
            //
            
            if (pk.pnext == null &&
                Interlocked.CompareExchange (ref state, null, pk) == pk) {
                return;
            }
            SlowDeregisterParker (pk);
        }

        //  
        // Sets the alerter.
        //

        internal bool Set() 
		{
            do {
                StParker s;
                if ((s = state) == ALERTED) {
                    return false;
                }

                //
                // The alerter isn't set; so, grab the alerter's list and alert
                // all parkers registered with the alerter.
                //

                if (Interlocked.CompareExchange (ref state, ALERTED, s) == s) {
                    while (s != null) {
                        if (s.TryCancel ()) {
                            s.Unpark (StParkStatus.Alerted);
                        }
                        s = s.pnext;
                    }
                    return true;
                }
            } while (true);
        }
    }
}
