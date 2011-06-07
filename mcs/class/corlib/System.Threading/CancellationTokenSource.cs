// 
// System.Threading.CancellationTokenSource.cs
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

using System.Collections.Generic;

#pragma warning disable 420

namespace System.Threading
{
    public sealed class CancellationTokenSource : IDisposable
    {
        private const CancelHandler NOT_CANCELLED = null;
        private static readonly CancelHandler CANNOT_BE_CANCELLED = new CancelHandler ();
        private static readonly CancelHandler CANCELLED = new CancelHandler ();
        private static readonly CancelHandler DISPOSED = new CancelHandler ();

        internal static readonly CancellationTokenSource CTS_CANCELLED =
            new CancellationTokenSource (true);

        internal static readonly CancellationTokenSource CTS_NOT_CANCELABLE =
            new CancellationTokenSource (false);

        private volatile CancelHandler state;
        private volatile ManualResetEvent evt;

        private List<CancellationTokenRegistration> ctrList;

        public CancellationTokenSource ()
        {
            state = NOT_CANCELLED;
        }

        private CancellationTokenSource (bool cancelled)
        {
            state = cancelled ? CANCELLED : CANNOT_BE_CANCELLED;
        }

        internal bool CanBeCanceled {
            get { return state != CANNOT_BE_CANCELLED; }
        }

        public bool IsCancellationRequested {
            get { return state == CANCELLED; }
        }

        public CancellationToken Token {
            get {
                ThrowIfDisposed ();
                return new CancellationToken (this);
            }
        }

        internal WaitHandle WaitHandle {
            get {
                ThrowIfDisposed ();

                if (evt != null) {
                    return evt;
                }

                var nevt = new ManualResetEvent (false);
                nevt = Interlocked.CompareExchange (ref evt, nevt, null) ?? nevt;

                if (IsCancellationRequested) {
                    nevt.Set ();
                }

                return nevt;
            }
        }

        public void Cancel ()
        {
            Cancel (false);
        }

        public void Cancel (bool throwOnFirstException)
        {
            ThrowIfDisposed ();

            CancelHandler ch = state;
            if (ch == CANCELLED || (ch = Interlocked.Exchange (ref state, CANCELLED)) == CANCELLED) {
                return;
            }

            if (ch == NOT_CANCELLED) {
                return;
            }

            List<Exception> exnList = null;
            CancelHandler chn;
            do {
                chn = ch.next;
                try {
                    ch.ExecuteCallback ();
                }
                catch (Exception exn) {
                    if (throwOnFirstException) {
                        throw;
                    }

                    if (exnList == null) {
                        exnList = new List<Exception> ();
                    }
                    exnList.Add (exn);
                }
            }
            while ((ch = chn) != NOT_CANCELLED);

            if (exnList != null) {
                throw new AggregateException (exnList);
            }
        }

        internal CancellationTokenRegistration InternalRegister (Action<object> callback,
                                                                 object cbState,
                                                                 SynchronizationContext sctx,
                                                                 ExecutionContext ectx)
        {
            CancelHandler ch = null;
            do {
                ThrowIfDisposed ();

                CancelHandler s;
                if ((s = state) == CANNOT_BE_CANCELLED) {
                    throw new InvalidOperationException (
                        "CancellationTokenSource can't be cancelled");
                }

                if (s == CANCELLED) {
                    break;
                }

                if (ch == null) {
                    ch = new CancelHandler (callback, cbState, sctx, ectx, this);
                }

                ch.next = s;
                if (Interlocked.CompareExchange (ref state, ch, s) == s) {
                    return new CancellationTokenRegistration (this, ch);
                }
            }
            while (true);

            //
            // We get here when cancellation already occurred; so, we run the
            // handler on the current thread and return an empty registration.
            //

            callback (state);
            return new CancellationTokenRegistration (null, null);
        }

        private static readonly Action<object> linkedCancel =
            arg => ((CancellationTokenSource) arg).Cancel (false);

        public static CancellationTokenSource CreateLinkedTokenSource (CancellationToken token1,
                                                                       CancellationToken token2)
        {
            var lcts = new CancellationTokenSource ();
            if (token1.CanBeCanceled) {
                lcts.ctrList = new List<CancellationTokenRegistration>
                {
                    token1.InternalRegister (linkedCancel, lcts)
                };
            }

            if (token2.CanBeCanceled) {
                if (lcts.ctrList == null) {
                    lcts.ctrList = new List<CancellationTokenRegistration> ();
                }
                lcts.ctrList.Add (token2.InternalRegister (linkedCancel, lcts));
            }

            return lcts;
        }

        public static CancellationTokenSource CreateLinkedTokenSource (
            params CancellationToken[] tokens)
        {
            if (tokens == null) {
                throw new ArgumentNullException ("tokens");
            }

            if (tokens.Length == 0) {
                throw new ArgumentException ("Cancellation tokens array is empty");
            }

            var lcts = new CancellationTokenSource ();

            for (int i = 0; i < tokens.Length; i++) {
                if (!tokens[i].CanBeCanceled) {
                    continue;
                }

                if (lcts.ctrList == null) {
                    lcts.ctrList = new List<CancellationTokenRegistration> ();
                }

                lcts.ctrList.Add (tokens[i].InternalRegister (linkedCancel, lcts));
            }
            return lcts;
        }

        public void Dispose ()
        {
            if (Interlocked.Exchange (ref state, DISPOSED) == DISPOSED) {
                return;
            }

            //
            // If this is a linked cancellation token source, unregister all cancel
            // handlers from the sources with which it is linked.
            //

            if (ctrList != null) {
                foreach (CancellationTokenRegistration ctr in ctrList) {
                    ctr.Dispose ();
                }
                ctrList = null;
            }
        }

        internal void UnlinkCancelHandler (CancelHandler tch)
        {
            if (state == tch &&
                Interlocked.CompareExchange (ref state, tch.next, tch) == tch) {
                return;
            }

            CancelHandler ch;
            if ((ch = state) == CANCELLED || ch == null) {
                return;
            }

            CancelHandler past;
            if ((past = tch.next) != null && past.state != 0) {
                past = past.next;
            }

            while (ch != null && ch != past) {
                CancelHandler chn = ch.next;
                if (chn != null && chn.state != 0) {
                    Interlocked.CompareExchange (ref ch.next, chn.next, chn);
                }
                else {
                    ch = chn;
                }
            }
        }

        private void ThrowIfDisposed ()
        {
            if (state == DISPOSED) {
                throw new ObjectDisposedException ("CancellationTokenSource");
            }
        }
    }
}

#endif