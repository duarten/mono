// System.Threading.ManualResetEventSlim.cs
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

namespace System.Threading
{
	[Diagnostics.DebuggerDisplayAttribute ("Set = {IsSet}")]
	public class ManualResetEventSlim : IDisposable
	{
		private const int defaultSpinCount = 256;
		internal readonly StNotificationEvent evt;
		private EventWaitHandle waitHandle;
		private bool isDisposed;

		public ManualResetEventSlim () 
            : this (false, defaultSpinCount) { }

		public ManualResetEventSlim (bool initialState) 
            : this (initialState, defaultSpinCount) { }

		public ManualResetEventSlim (bool initialState, int spinCount)
		{
			if (spinCount < 0) {
				throw new ArgumentOutOfRangeException ("spinCount is less than 0", "spinCount");
			}

			evt = new StNotificationEvent (initialState, spinCount);
		}

		public bool IsSet {
			get { return evt.IsSet; }
		}

		public int SpinCount {
			get { return evt.spinCount; }
		}

		/*
		 * If a user disposes of the WaitHandle, the behavior is undefined.
		 */

		public WaitHandle WaitHandle {
			get {
				ThrowIfDisposed ();
				EventWaitHandle mre;
				if ((mre = waitHandle) == null) {
					mre = new EventWaitHandle (evt);
					mre = Interlocked.CompareExchange (ref waitHandle, mre, null) ?? mre;
				}
				return mre;
			}
		}

		public void Reset ()
		{
			ThrowIfDisposed ();
		   evt.Reset ();
		}

		public void Set ()
		{
			ThrowIfDisposed ();
		   evt.Set ();
		}

		public void Wait ()
		{
			Wait (CancellationToken.None);
		}

		public bool Wait (int millisecondsTimeout)
		{
			return Wait (millisecondsTimeout, CancellationToken.None);
		}

		public void Wait (CancellationToken cancellationToken)
		{
			Wait (Timeout.Infinite, cancellationToken);
		}

		public bool Wait (TimeSpan timeout)
		{
			long totalMilliseconds = (long)timeout.TotalMilliseconds;
         if (totalMilliseconds < -1 || totalMilliseconds > int.MaxValue) {
				throw new ArgumentOutOfRangeException ("timeout"); 
         }

			return Wait ((int)totalMilliseconds, CancellationToken.None);
		}

		public bool Wait (TimeSpan timeout, CancellationToken cancellationToken)
		{
			long totalMilliseconds = (long)timeout.TotalMilliseconds;
         if (totalMilliseconds < -1 || totalMilliseconds > int.MaxValue) {
				throw new ArgumentOutOfRangeException ("timeout"); 
         }

			return Wait ((int)totalMilliseconds, cancellationToken);
		}

		public bool Wait (int millisecondsTimeout, CancellationToken cancellationToken)
		{
			ThrowIfDisposed ();

			if (millisecondsTimeout < -1) {
				throw new ArgumentOutOfRangeException ("millisecondsTimeout");
			}

			return evt.TryWaitOne (new StCancelArgs (millisecondsTimeout, cancellationToken));
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
				if (waitHandle != null) {
					waitHandle.Dispose ();
					waitHandle = null;
				}
			}
		}

		private void ThrowIfDisposed ()
		{
			if (isDisposed) {
				throw new ObjectDisposedException("ManualResetEventSlim");
			}
		}

		#endregion
	}
}

#endif