// 
// LazyInitializer.cs
//  
// Author:
//       Jérémie "Garuma" Laval <jeremie.laval@gmail.com>
//			Duarte Nunes <duarte.m.nunes@gmail.com>
// 
// Copyright (c) 2009 Jérémie "Garuma" Laval
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

#pragma warning disable 0219
#pragma warning disable 0420

namespace System.Threading
{
	public static class LazyInitializer
	{
		/*
		 * Caches delegate instances used to create objects of type T.
		 */

      private static class FactoryCache<T>
      {
         internal static Func<T> Factory = () => { 
            try { 
               return (T)Activator.CreateInstance (typeof (T));
            } catch (MissingMethodException) { 
               throw new MissingMemberException (string.Format ("No parameterless ctor for the type {0}", typeof (T).Name));
            }
         };
      }

		/*
       * The object reference used to generate acquire memory barriers to avoid
		 * LD/LD reorderings on weakly ordered architectures, specifically due to
		 * speculative loads and branch prediction.
       */

      private static volatile object barrier = typeof (LazyInitializer);

		public static T EnsureInitialized<T> (ref T target) where T : class
		{
			if (target != null) {
            object acquire = barrier;
            return target;
         }

			return SlowEnsureInitialized (ref target, FactoryCache<T>.Factory);
		}
		
		public static T EnsureInitialized<T> (ref T target, Func<T> valueFactory) where T : class
		{
			if (target != null) {
            object acquire = barrier;
            return target;
         }

			return SlowEnsureInitialized (ref target, valueFactory);
		}

		private static T SlowEnsureInitialized<T> (ref T target, Func<T> valueFactory) where T : class
		{
			T v = valueFactory ();
			if (v == null) {
				throw new InvalidOperationException ("Factory method returned a null reference");
			}

			return Interlocked.CompareExchange (ref target, v, null) ?? v;
		}

		public static T EnsureInitialized<T> (ref T target, ref bool initialized, ref object syncLock)
		{
			if (initialized) {
            object acquire = barrier;
            return target;
         }

			return SlowEnsureInitialized (ref target, ref initialized, ref syncLock, FactoryCache<T>.Factory);
		}
		
		public static T EnsureInitialized<T> (ref T target, ref bool initialized, ref object syncLock, Func<T> valueFactory)
		{
			if (initialized) {
            object acquire = barrier;
            return target;
         }

			return SlowEnsureInitialized (ref target, ref initialized, ref syncLock, valueFactory);
		}

		public static T SlowEnsureInitialized<T> (ref T target, ref bool initialized, ref object syncLock, Func<T> valueFactory)
		{
			object @lock;
			if ((@lock = syncLock) == null) {
				var nlock = new object();
				@lock = Interlocked.CompareExchange (ref syncLock, nlock, null) ?? nlock;
			}

			lock (@lock) {
				if (initialized) {
					return target;
				}

				initialized = true;
				return target = valueFactory ();
			}
		}
	}
}

#endif