//
// System.Threading.StWaitBlock.cs
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
    internal enum WaitType { WaitAll, WaitAny };

    //
    // The wait block used with waitables, locks and condition variables.
    //

    internal sealed class StWaitBlock
    {
        internal static readonly StWaitBlock SENTINEL = new StWaitBlock ();

        internal volatile StWaitBlock next;

        internal readonly StParker parker;
        internal readonly WaitType waitType;

        internal const int LOCKED_REQUEST = (1 << 31);
        internal const int SPECIAL_REQUEST = (1 << 30);
        internal const int MAX_REQUEST = (SPECIAL_REQUEST - 1);
        internal volatile int request;

        /*
         * The wait status specified when the owner thread of the wait
         * block is unparked.
         */

        internal readonly int waitKey;

        /*
         * Constructor used with sentinel wait blocks.
         */

        internal StWaitBlock ()
        {
            waitType = WaitType.WaitAny;
            request = 0x13081953;
            waitKey = StParkStatus.Success;
        }

        internal StWaitBlock (StParker pk, WaitType t, int r, int k)
        {
            parker = pk;
            waitType = t;
            request = r;
            waitKey = k;
        }

        internal StWaitBlock (WaitType t, int r, int k)
        {
            parker = new StParker ();
            waitType = t;
            request = r;
            waitKey = k;
        }

        internal StWaitBlock (WaitType t, int r)
        {
            parker = new StParker ();
            waitType = t;
            request = r;
        }

        internal StWaitBlock (WaitType t)
        {
            parker = new StParker ();
            waitType = t;
        }

        internal StWaitBlock (int r)
        {
            parker = new StParker ();
            request = r;
        }

        internal StWaitBlock (StParker pk, int r)
        {
            parker = pk;
            request = r;
        }

       internal bool CasNext (StWaitBlock n, StWaitBlock nn)
        {
            return (next == n && Interlocked.CompareExchange (ref next, nn, n) == n);
        }
    }

    /*
     * A non-thread-safe queue of wait blocks.
     */

    internal struct WaitBlockQueue {
        internal StWaitBlock head;
        private StWaitBlock tail;

        internal void Enqueue(StWaitBlock wb) {
            if (head == null) {
                head = wb;
            } else {
                tail.next = wb;
            }
            tail = wb;
        }

        internal StWaitBlock Dequeue() {
            StWaitBlock wb;
            if ((wb = head) == null) {
                return null;
            }

            if ((head = wb.next) == null) {
                tail = null;
            }
            return wb;
        }

        internal void Remove(StWaitBlock wb) {

            if (wb.next == wb) {
                return;
            }

            StWaitBlock p = head;
            StWaitBlock pv = null;
            while (p != null) {
                if (p == wb) {
                    if (pv == null) {
                        if ((head = wb.next) == null) {
                            tail = null;
                        }
                    } else {
                        if ((pv.next = wb.next) == null)
                            tail = pv;
                    }
                    wb.next = wb;
                    return;
                }
                pv = p;
                p = p.next;
            }
            
            throw new InvalidOperationException();
        }
    }

   /*
    * A queue of wait blocks that allows non-blocking enqueue
    * and lock-protected dequeue.
    */

   internal struct LockedWaitQueue
   {
      internal volatile StWaitBlock head;
      internal volatile StWaitBlock tail;

      private const int FREE = 0;
      private const int BUSY = 1;
      private volatile int qlock;

      internal void Init ()
      {
         head = tail = new StWaitBlock ();
      }

      internal StWaitBlock First {
         get { return qlock == FREE ? head.next : null; }
      }

      internal bool IsEmpty {
         get { return head.next == null; }
      }

		internal bool TryLock ()
      {
         return qlock == FREE && Interlocked.CompareExchange (ref qlock, BUSY, FREE) == FREE;
      }

      internal bool Enqueue (StWaitBlock wb)
      {
         do {
				StWaitBlock t = tail;

				if (t == null) {
					wb.next = wb;
					return false; // Useful for the inflate operation.
				}

				StWaitBlock tn = t.next;

				if (tn != null) {
					AdvanceTail (t, tn);
					continue;
				}

				if (t.CasNext (null, wb)) {
					AdvanceTail (t, wb);
					return t == head;
				}
         } while (true);
      }

     internal void SetHeadAndUnlock (StWaitBlock nh)
		{
			do {
				StWaitBlock w;
            if ((w = nh.next) == null || !w.parker.IsLocked || w.request < 0) {
               break;
            }
            nh.next = nh; // Mark old head's wait block as unlinked.
            nh = w;
         } while (true);

         head = nh;
         Interlocked.Exchange (ref qlock, FREE);
      }

      internal void Unlink (StWaitBlock wb)
      {
         if (wb.next == wb || wb == head) {
            return;
         }

         StWaitBlock n;
         StWaitBlock pv = head;
         while ((n = pv.next) != wb) {
            if (n.parker.IsLocked) {
               pv.next = n.next;
               n.next = n;
            } else {
               pv = n;
            }
         }

         do {
            pv.next = n.next;
            n.next = n;
         } while ((n = pv.next).next != null && n.parker.IsLocked);
      }

		private void AdvanceTail (StWaitBlock t, StWaitBlock nt)
      {
			if (tail == t) {
				Interlocked.CompareExchange (ref tail, nt, t);
         }
      }
	}
}
