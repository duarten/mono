// CountdownEvent.cs
//
// Copyright (c) 2008 Jérémie "Garuma" Laval
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
//
//

#if NET_4_0 || MOBILE

#pragma warning disable 0420 

namespace System.Threading
{
	[Diagnostics.DebuggerDisplayAttribute ("Initial Count={InitialCount}, Current Count={CurrentCount}")]
	public class CountdownEvent : IDisposable
	{
		private volatile int count;
		private readonly ManualResetEventSlim evt;
		private bool isDisposed;
		
		public CountdownEvent (int initialCount) 
		{
			if (initialCount < 0) { 
				throw new ArgumentOutOfRangeException ("initialCount", "initialCount is negative");
			}
			
			count = InitialCount = initialCount;
			evt = new ManualResetEventSlim (false);
		}

		public int CurrentCount {
			get { return count; }
		}

		public int InitialCount { get; private set; }

		public bool IsSet {
			get { return count == 0; }
		}
		
		public WaitHandle WaitHandle {
			get { return evt.WaitHandle; }
		}
		
		public bool Signal ()
		{
			return Signal (1);
		}
		
		public bool Signal (int signalCount)
		{
			ThrowIfDisposed (); 

			if (signalCount <= 0) {
				throw new ArgumentOutOfRangeException ("signalCount");
			}

			var spin = new SpinWait ();
			do {
				int c = count;
		      
				if (c < signalCount) {
		      	throw new InvalidOperationException ("Cannot decrement CountDownEvent below zero.");
		      }
            
				if (Interlocked.CompareExchange (ref count, c - signalCount, c) == c) {
			      if (c == signalCount) {
						evt.Set ();
						return true;
			      }
			      return false;
		      }

				spin.SpinOnce ();
			} while (true);
		}
		
		public void AddCount ()
		{
			AddCount (1);
		}
		
		public void AddCount (int signalCount)
		{
			if (!TryAddCount (signalCount)) {
				throw new InvalidOperationException ("The count is already zero");
			}
		}
		
		public bool TryAddCount ()
		{
			return TryAddCount (1);
		}
		
		public bool TryAddCount (int signalCount)
		{
			ThrowIfDisposed ();

			if (signalCount <= 0) {
				throw new ArgumentOutOfRangeException ("signalCount");
			}

			var spin = new SpinWait ();

			do {
				int c;
            if ((c = count) == 0) {
               return false;
            }

				if (c > Int32.MaxValue - signalCount) {
               throw new InvalidOperationException ("Increment overflow"); 
            }
				
				if (Interlocked.CompareExchange (ref count, c + signalCount, c) == c) {
               return true;
            }

				spin.SpinOnce ();
         } while (true);
		}
		
		public void Wait ()
		{
			Wait (CancellationToken.None);
		}

		public bool Wait (int millisecondsTimeout)
		{
			return evt.Wait (millisecondsTimeout);
		}

		public void Wait (CancellationToken cancellationToken)
		{
			evt.Wait (cancellationToken);
		}

		public bool Wait (TimeSpan timeout)
		{
			return evt.Wait (timeout);
		}

		public bool Wait (TimeSpan timeout, CancellationToken cancellationToken)
		{
			return evt.Wait (timeout, cancellationToken);
		}

		public bool Wait (int millisecondsTimeout, CancellationToken cancellationToken)
		{
			return evt.Wait (millisecondsTimeout, cancellationToken);
		}

		public void Reset ()
		{
			Reset (InitialCount);
		}
		
		public void Reset (int count)
		{
			ThrowIfDisposed();
 
         if (count < 0) { 
				throw new ArgumentOutOfRangeException("count");
         }

         InitialCount = count; 
         this.count = count;
 
         if (count == 0)  {
				evt.Set(); 
         } else {
				evt.Reset(); 
         }
		}
		
      #region IDisposable implementation

      public void Dispose ()
		{
			Dispose (true);
			GC.SuppressFinalize (this);
		}

		protected virtual void Dispose (bool disposing) 
		{
			if (disposing && !isDisposed) {
				isDisposed = true;
				evt.Dispose ();
			}
		}

		private void ThrowIfDisposed ()
		{
			if (isDisposed) {
				throw new ObjectDisposedException ("ManualResetEventSlim");
			}
		}

		#endregion
	}
}

#endif