// 
// CancellationTokenSource.cs
//  
// Author:
//			Jérémie "Garuma" Laval <jeremie.laval@gmail.com>
//			Duarte Nunes <duarte.m.nunes@gmail.com>
// 
// Copyright (c) 2009 Jérémie "Garuma" Laval
// Copyright (c) 2011 Duarte Nunes
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

#if NET_4_0 || MOBILE

using System.Collections.Generic;

#pragma warning disable 420

namespace System.Threading
{

	public sealed class CancellationTokenSource : IDisposable
   {
		private const StParker NOT_CANCELLED = null;
      private static readonly StParker CANNOT_BE_CANCELLED = new SentinelParker ();
		private static readonly StParker CANCELLED = new SentinelParker ();
		private static readonly StParker DISPOSED = new SentinelParker ();

		internal static readonly CancellationTokenSource CTS_CANCELLED =
         new CancellationTokenSource (true);

      internal static readonly CancellationTokenSource CTS_NOT_CANCELABLE =
         new CancellationTokenSource (false);

      private volatile StParker state;
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

      public CancellationToken Token {
         get {
            ThrowIfDisposed ();
            return new CancellationToken (this);
         }
      }
		
      internal bool CanBeCanceled {
         get { return state != CANNOT_BE_CANCELLED; }
      }

      public bool IsCancellationRequested {
         get { return state == CANCELLED; }
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

		public void Cancel (bool throwOnFirstException)
      {
			ThrowIfDisposed ();

         StParker pk = state;
         if (pk == CANCELLED || (pk = Interlocked.Exchange (ref state, CANCELLED)) == CANCELLED) {
            return;
         }

         List<Exception> exnList = null;
			while (pk != NOT_CANCELLED) {
         	StParker pkn = pk.pnext;
            try {
					if (pk.TryCancel ()) {
						pk.Unpark (StParkStatus.Cancelled);
					}
            } catch (Exception exn) {
               if (throwOnFirstException) {
                  throw;
               }

               if (exnList == null) {
                  exnList = new List<Exception> ();
               }
               exnList.Add (exn);
            }

         	pk.pnext = null;
         	pk = pkn;
         }

         if (exnList != null) {
            throw new AggregateException (exnList);
         }
      }

		public void Cancel ()
		{
			Cancel (false);
		}
		
		internal bool RegisterParker (StParker pk) 
		{
			ThrowIfDisposed ();

			if (state == CANNOT_BE_CANCELLED) {
            throw new InvalidOperationException ("CancellationTokenSource can't be cancelled");
         }

			do {
            StParker s;

            if ((s = state) == CANCELLED) {
               return false;
            }

            pk.pnext = s;
            if (Interlocked.CompareExchange (ref state, pk, s) == s) {
               return true;
            }
         } while (true);
		}

		internal CancellationTokenRegistration RegisterInternal (Action<Object> callback, 
																					object cbState,
																					bool useSynchronizationContext,
																					bool useExecutionContext)
      {
			if (!CanBeCanceled) {
				return new CancellationTokenRegistration ();
			}

         var cbParker = new CbParker (_ => callback (cbState), useSynchronizationContext,
      	                             useExecutionContext);

			if (RegisterParker (cbParker) && 
				 cbParker.EnableCallback (Timeout.Infinite) == StParkStatus.Pending) {
				return new CancellationTokenRegistration (cbParker, this);
			}

			callback (cbState);
			return new CancellationTokenRegistration ();
      }

		internal void DeregisterParker (StParker pk) 
		{
			if (pk.pnext == null && Interlocked.CompareExchange (ref state, null, pk) == pk) {
				return;
			}
			SlowDeregisterParker (pk);
		}

		private void SlowDeregisterParker (StParker pk) 
		{
         StParker next;
         if ((next = pk.pnext) != null && next.IsLocked) {
            next = next.pnext;
         }

         StParker p = state;

         while (p != null && p != next && state != null && !(state is SentinelParker)) {
				StParker n = p.pnext;
            if (n != null && n.IsLocked) {
               p.CasNext (n, n.pnext);
            } else {
               p = n;
            }
         }     
      }

		private static readonly Action<object> linkedCancel =
         arg => ((CancellationTokenSource) arg).Cancel (false);

      public static CancellationTokenSource CreateLinkedTokenSource (CancellationToken token1,
                                                                     CancellationToken token2)
      {
         var lcts = new CancellationTokenSource ();

			/*
			 * We must keep a list of all registrations so we can dispose of them.
			 */

         if (token1.CanBeCanceled) {
            lcts.ctrList = new List<CancellationTokenRegistration>
            {
               token1.cts.RegisterInternal (linkedCancel, lcts, false, false)
            };
         }

			if (token2.CanBeCanceled) {
            (lcts.ctrList ?? (lcts.ctrList = new List<CancellationTokenRegistration> ()))
					.Add (token2.cts.RegisterInternal (linkedCancel, lcts, false, false));
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
            if (tokens[i].CanBeCanceled) {
               (lcts.ctrList ?? (lcts.ctrList = new List<CancellationTokenRegistration> ()))
						.Add (tokens [i].cts.RegisterInternal (linkedCancel, lcts, false, false));
            }
         }
         return lcts;
      }

      public void Dispose ()
      {
			StParker p;
         if ((p = Interlocked.Exchange (ref state, DISPOSED)) == DISPOSED) {
            return;
         }

			while (p != NOT_CANCELLED) 
			{
				StParker pkn = p.pnext;
				p.pnext = null;
				p = pkn;
			}

         /*
          * If this is a linked cancellation token source, unregister all callbacks
			 * that we added to the linked tokens.
          */

         if (ctrList != null) {
            foreach (CancellationTokenRegistration ctr in ctrList) {
               ctr.Dispose ();
            }
            ctrList = null;
         }
      }

      internal void ThrowIfDisposed ()
      {
         if (state == DISPOSED) {
            throw new ObjectDisposedException ("CancellationTokenSource");
         }
      }
   }
}

#endif