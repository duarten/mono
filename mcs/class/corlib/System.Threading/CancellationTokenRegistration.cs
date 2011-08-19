// 
// CancellationTokenRegistration.cs
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

#pragma warning disable 420

namespace System.Threading
{

	public struct CancellationTokenRegistration : IDisposable,
                                          IEquatable<CancellationTokenRegistration>
   {
		private readonly CbParker cbparker;
      private readonly CancellationTokenSource cts;

      internal CancellationTokenRegistration (CbParker cbparker,
                                              CancellationTokenSource cts)
      {
         this.cts = cts;
         this.cbparker = cbparker;
      }

      public void Dispose ()
      {
         if (cbparker != null) {
         	if (cbparker.TryCancel ()) {
         		cts.DeregisterParker (cbparker);
         	} else {
         		cts.ThrowIfDisposed ();
         	}
         }
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
         return cts == other.cts && cbparker == other.cbparker;
      }

      public override int GetHashCode ()
      {
         return cts != null 
				  ? cts.GetHashCode () ^ cbparker.GetHashCode () 
				  : CancellationTokenSource.CTS_NOT_CANCELABLE.GetHashCode ();
      }
   }
}

#endif