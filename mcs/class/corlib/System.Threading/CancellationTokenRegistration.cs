//
// System.Threading.CancellationTokenRegistration.cs
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

#pragma warning disable 420

namespace System.Threading
{
    internal class CancelHandler
    {
        internal volatile CancelHandler next;

        private readonly Action<object> callback;
        private readonly object cbState;
        private readonly SynchronizationContext sctx;
        private readonly ExecutionContext ectx;
        private readonly CancellationTokenSource cts;

        //
        // The state of the cancel handler. It start with zero and
        // is set to one before the callback is called or when the
        // cancel handler is deregistered.
        //

        internal volatile int state;

        //
        // The notification event that is set when the cancel handler is
        // deregistered or called.
        //

        private NotificationEvent free = new NotificationEvent(false, 200);

        //
        // The ID of the thread that is executing the cancel handler.
        //

        private int ctid;

        internal CancelHandler (Action<object> callback, object state,
                                SynchronizationContext sctx, ExecutionContext ectx,
                                CancellationTokenSource cts)
        {
            this.callback = callback;
            this.cbState = state;
            this.sctx = sctx;
            this.ectx = ectx;
            this.cts = cts;
        }

        internal CancelHandler ()
        {}

        private bool TryLock ()
        {
            return state == 0 && Interlocked.Exchange (ref state, 1) == 0;
        }

        private static readonly SendOrPostCallback toSynchContext =
            arg => ((CancelHandler) arg).ExecuteCancelHandler ();

        internal void ExecuteCallback ()
        {
            if (TryLock ()) {
                if (sctx != null) {
                    sctx.Send (toSynchContext, this);
                }
                else {
                    ExecuteCancelHandler ();
                }
            }
        }

        private static readonly ContextCallback toExecContext = arg =>
        {
            var ch = (CancelHandler) arg;
            ch.ctid = Thread.CurrentThreadId;
            try {
                ch.callback (ch.cbState);
                ch.ctid = 0;
            } finally {
                ch.free.Set ();
            }
        };

        internal void ExecuteCancelHandler ()
        {
            if (ectx != null) {
                ExecutionContext.Run (ectx, toExecContext, this);
            } else {
                toExecContext (this);
            }
        }

        //
        // The return value from this method informs whether the cancel 
        // handler was or not deregistered due to the call to this method.
        //

        internal void Deregister ()
        {
            //
            // If we are being called from the cancel handler, return false.
            //

            if (ctid == Thread.CurrentThreadId) {
                return;
            }

            //
            // If we lock the cancel handler - which prevents it from being called -,
            // we try to unlink the cancel handler object from the cancellation
            // source list; otherwise, the handler is about to be called and we
            // need to wait until *free* is signalled, which means that the
            // callback finished executing.
            //

            if (TryLock()) {
                free.Set();
                cts.UnlinkCancelHandler(this);
            } else {
                free.Wait (StCancelArgs.None);
            }
        }
    }

    public struct CancellationTokenRegistration : IDisposable,
                                                  IEquatable<CancellationTokenRegistration>
    {
        private readonly CancelHandler ch;
        private readonly CancellationTokenSource cts;

        internal CancellationTokenRegistration (CancellationTokenSource cts,
                                                CancelHandler ch)
        {
            this.cts = cts;
            this.ch = ch;
        }

        //
        // If the target callback is currently executing, Deregister will wait
        // until it completes, except in the degenerate cases where a callback
        // method deregisters itself.
        //

        public void Dispose ()
        {
            ch.Deregister ();
        }

        public static bool operator == (CancellationTokenRegistration left,
                                        CancellationTokenRegistration right)
        {
            return left.Equals (right);
        }

        public static bool operator != (CancellationTokenRegistration left,
                                        CancellationTokenRegistration right)
        {
            return !left.Equals (right);
        }

        public override bool Equals (object other)
        {
            return other is CancellationTokenRegistration &&
                   Equals ((CancellationTokenRegistration) other);
        }

        public bool Equals (CancellationTokenRegistration other)
        {
            return cts == other.cts && ch == other.ch;
        }

        public override int GetHashCode ()
        {
            return cts != null ? cts.GetHashCode () ^ ch.GetHashCode () : ch.GetHashCode ();
        }
    }
}

#endif