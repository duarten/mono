//
// System.Threading.ManualResetEventSlim.cs
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
	[Diagnostics.DebuggerDisplayAttribute ("Set = {IsSet}")]
	public class ManualResetEventSlim : IDisposable
	{
		private const int defaultSpinCount = 100;

		private readonly StNotificationEvent evt;

		public ManualResetEventSlim () 
            : this (false, defaultSpinCount) { }

		public ManualResetEventSlim (bool initialState) 
            : this (initialState, defaultSpinCount) { }

		public ManualResetEventSlim (bool initialState, int spinCount)
		{
			if (spinCount < 0)
				throw new ArgumentOutOfRangeException ("spinCount is less than 0", "spinCount");

		    evt = new StNotificationEvent (initialState, spinCount);
		}

		public bool IsSet {
			get { return evt.IsSet; }
		}

		public int SpinCount {
			get { return evt.waitEvent.spinCount; }
		}

        //
        // Just return a new EventWaitHandle. We're assuming that the object 
        // will be short lived, so we don't hang on to it. The user might
        // try to set the Handle/SafeWaitHandle properties, but that leads
        // to undefined behavior so we don't even worry about it.
        //

		public WaitHandle WaitHandle {
			get { return new EventWaitHandle (evt); }
		}

		public void Reset ()
		{
		    evt.Reset ();
		}

		public void Set ()
		{
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

		public bool Wait (TimeSpan timeout)
		{
			return Wait ((int)timeout.TotalMilliseconds, CancellationToken.None);
		}

		public void Wait (CancellationToken cancellationToken)
		{
			Wait (-1, cancellationToken);
		}

		public bool Wait (int millisecondsTimeout, CancellationToken cancellationToken)
		{
			if (millisecondsTimeout < -1)
				throw new ArgumentOutOfRangeException ("millisecondsTimeout",
				                                       "millisecondsTimeout is a negative number other than -1");

		    var alerter = cancellationToken.CanBeCanceled ? new StAlerter () : null;

            using (cancellationToken.Register(StAlerter.CancellationTokenCallback, alerter)) {
                try {
                    return evt.Wait(new StCancelArgs (millisecondsTimeout, alerter));
                } catch (StThreadAlertedException) {
                    cancellationToken.ThrowIfCancellationRequested ();
                    return false; /* Shut the compiler up */
                }
            }
		}

		public bool Wait (TimeSpan timeout, CancellationToken cancellationToken)
		{
			return Wait ((int)timeout.TotalMilliseconds, cancellationToken);
		}

		#region IDisposable implementation

        public void Dispose ()
		{
			Dispose (true);
		}

		protected virtual void Dispose (bool disposing)
		{ }

		#endregion
	}
}
#endif
