//
// System.Threading.CancellationToken.cs
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

#if NET_4_0 || MOBILE

namespace System.Threading
{
    [Diagnostics.DebuggerDisplay ("IsCancellationRequested = {IsCancellationRequested}")]
    public struct CancellationToken
    {
        private static readonly Action<object> callArgAsAction = arg => ((Action) arg)();

        private readonly CancellationTokenSource cts;

        public CancellationToken (bool canceled)
            : this (canceled ? CancellationTokenSource.CTS_CANCELLED
                             : CancellationTokenSource.CTS_NOT_CANCELABLE) { }

        internal CancellationToken (CancellationTokenSource cts)
        {
            this.cts = cts;
        }

        public static CancellationToken None {
            get { return new CancellationToken (); }
        }

        public bool IsCancellationRequested {
            get { return cts != null && cts.IsCancellationRequested; }
        }

        public bool CanBeCanceled {
            get { return cts != null && cts.CanBeCanceled; }
        }

        public WaitHandle WaitHandle {
            get { return cts.WaitHandle; }
        }

        public CancellationTokenRegistration Register (Action callback)
        {
            if (callback == null) {
                throw new ArgumentNullException ("callback");
            }

            return Register (callArgAsAction, callback, false, true);
        }

        public CancellationTokenRegistration Register (Action callback,
                                                       bool useSynchronizationContext)
        {
            if (callback == null) {
                throw new ArgumentNullException ("callback");
            }

            return Register (callArgAsAction, callback, useSynchronizationContext, true);
        }

        public CancellationTokenRegistration Register (Action<Object> callback, object state)
        {
            return Register (callback, state, false, true);
        }

        public CancellationTokenRegistration Register (Action<Object> callback, Object state,
                                                       bool useSynchronizationContext)
        {
            return Register (callback, state, useSynchronizationContext, true);
        }

        internal CancellationTokenRegistration InternalRegister (Action<object> callback,
                                                                 object state)
        {
            return Register (callback, state, false, false);
        }

        private CancellationTokenRegistration Register (Action<Object> callback, object state,
                                                        bool useSynchronizationContext,
                                                        bool useEctx)
        {
            if (callback == null) {
                throw new ArgumentNullException ("ccb");
            }

            if (!CanBeCanceled) {
                return new CancellationTokenRegistration ();
            }

            SynchronizationContext sctx = null;
            ExecutionContext ectx = null;

            if (!IsCancellationRequested) {
                if (useSynchronizationContext) {
                    sctx = SynchronizationContext.Current;
                }

                if (useEctx) {
                    ectx = ExecutionContext.Capture ();
                }
            }

            return cts.InternalRegister (callback, state, sctx, ectx);
        }

        public void ThrowIfCancellationRequested ()
        {
            if (IsCancellationRequested) {
                throw new OperationCanceledException (this);
            }
        }

        public bool Equals (CancellationToken other)
        {
            return cts == other.cts;
        }

        public override bool Equals (object other)
        {
            return other is CancellationToken && Equals ((CancellationToken) other);
        }

        public override int GetHashCode ()
        {
            return cts == null
                       ? CancellationTokenSource.CTS_NOT_CANCELABLE.GetHashCode ()
                       : cts.GetHashCode ();
        }

        public static bool operator == (CancellationToken left, CancellationToken right)
        {
            return left.Equals (right);
        }

        public static bool operator != (CancellationToken left, CancellationToken right)
        {
            return !left.Equals (right);
        }
    }
}

#endif