// SemaphoreSlim.cs
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
	[Diagnostics.DebuggerDisplayAttribute ("Current Count = {currCount}")]
	public class SemaphoreSlim : IDisposable
	{
		private const int defaultSpinCount = 256;
	   private readonly StSemaphore sem;
		private volatile ManualResetEvent waitHandle;
		private bool isDisposed;

		public SemaphoreSlim (int initialCount) 
         : this (initialCount, int.MaxValue) { }

		public SemaphoreSlim (int initialCount, int maxCount)
		{
			if (initialCount < 0 || initialCount > maxCount || maxCount < 0) {
			    throw new ArgumentOutOfRangeException ("initialCount");
			}

		   if (maxCount < 0) {
             throw new ArgumentOutOfRangeException ("maxCount");
         }

		   sem = new StSemaphore (initialCount, maxCount, defaultSpinCount);
		}

		public int CurrentCount {
			get { return sem.CurrentCount; }
		}

      public WaitHandle AvailableWaitHandle {
			get {
				ThowIfDisposed ();
				ManualResetEvent mre;
				if ((mre = waitHandle) == null) {
					mre = new ManualResetEvent (false);
					ManualResetEvent nmre;
					if ((nmre = Interlocked.CompareExchange (ref waitHandle, mre, null)) == null) {
						if (CurrentCount > 0) {
							mre.Set();
						}
					} else {
						mre = nmre;
					}
				}
				return mre;
			}
		}

		public int Release (int releaseCount)
		{
			ThowIfDisposed ();
			int oldCount = sem.Release (releaseCount);
			if (oldCount == 0 && waitHandle != null) {
				waitHandle.Set ();
			}
			return oldCount;
		}

		public int Release ()
		{
			return Release (1);
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
			ThowIfDisposed ();

			if (millisecondsTimeout < -1) {
				throw new ArgumentOutOfRangeException ("millisecondsTimeout");
			}

			bool success = sem.TryWait (1, new StCancelArgs (millisecondsTimeout, cancellationToken));
			if (success) {
				
				/*
				 * It's OK if the event is set but there are no permits, because there is
				 * an implied race between waiting on the wait handle and waiting on the 
				 * semaphore. However, it's not OK if there are permits but the event is
				 * not set. The second test of CurrentCount ensures that won't happen.
				 */

				if (sem.CurrentCount == 0 && waitHandle != null) {
					waitHandle.Reset ();
					if (sem.CurrentCount > 0) {
						waitHandle.Set ();
					}
				}
			}
			return success;
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

		private void ThowIfDisposed ()
		{
			if (isDisposed) {
				throw new ObjectDisposedException("SemaphoreSlim");
			}
		}

		#endregion
	}
}

#endif