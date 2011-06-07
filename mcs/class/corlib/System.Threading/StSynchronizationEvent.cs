//
// System.Threading.StSynchronizationEvent.cs
//
// Copyright 2011 Carlos Martins, Duarte Nunes
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

#pragma warning disable 0420

namespace System.Threading
{
    internal sealed class StSynchronizationEvent : StMutant
    {
        internal StSynchronizationEvent (bool initialState, int spinCount) 
            : base (initialState, spinCount) { }

        internal StSynchronizationEvent (bool initialState) 
            : base (initialState, 0) { }

        internal StSynchronizationEvent () 
            : base (false, 0) { }

        public bool Wait (StCancelArgs cargs)
        {
            return SlowTryAcquire (cargs);
        }

        public void Wait ()
        {
            SlowTryAcquire (StCancelArgs.None);
        }

        public bool Set ()
        {
            return _Release ();
        }

        public bool Reset ()
        {
            return _TryAcquire ();
        }
    }
}