// 
// CancellationToken.cs
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

namespace System.Threading
{
   [Diagnostics.DebuggerDisplay ("IsCancellationRequested = {IsCancellationRequested}")]

	public struct CancellationToken
   {
		private static readonly Action<object> callArgAsAction = arg => ((Action) arg)();

      internal readonly CancellationTokenSource cts;

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

      public CancellationTokenRegistration Register (Action<object> callback, object state)
      {
         return Register (callback, state, false, true);
      }

      public CancellationTokenRegistration Register (Action<object> callback, object state,
                                                     bool useSynchronizationContext)
      {
         return Register (callback, state, useSynchronizationContext, true);
      }

      private CancellationTokenRegistration Register (Action<object> callback, object state,
                                                      bool useSynchronizationContext,
                                                      bool useExecutionContext)
      {
         if (callback == null) {
            throw new ArgumentNullException ("callback");
         }

			return cts != null 
				  ? cts.RegisterInternal (callback, state, useSynchronizationContext, useExecutionContext)
				  : new CancellationTokenRegistration ();
      }

		internal CancellationTokenRegistration RegisterInternal (Action<object> callback, object state)
		{
			return cts != null 
				  ? cts.RegisterInternal (callback, state, false, false)
				  : new CancellationTokenRegistration ();
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