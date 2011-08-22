/* 
 * st-mutant.c: Base for the synchronization event and fair lock.
 *
 * Author: 
 * Duarte Nunes (duarte.m.nunes@gmail.com)
 */

#include <mono/utils/st.h>

#define ACQUIRE	1

/*
 * Here we ensure that we never lose a signal, which are conflated only when
 * there are no waiters.
 */
gboolean
st_mutant_release_waiters_and_unlock_queue (StMutant *mutant, gboolean try_set)
{
   ListEntry *entry, *head;
   WaitBlock *wait_block;
   StParker *parker;
   gboolean try_set_used;

	head = &mutant->queue.head;
	try_set_used = FALSE;

   do {
		while ((mutant->state != 0 || try_set) && (entry = head->flink) != head) {
			wait_block = CONTAINING_RECORD (entry, WaitBlock, wait_list_entry);         
			parker = wait_block->parker;

			if (try_set || (mutant->state == 1 && InterlockedCompareExchange (&mutant->state, 0, 1) == 1)) {
            
				if (try_set) {
               try_set = FALSE;
               try_set_used = TRUE;
            }

				st_list_remove_entry (entry);

				if (st_parker_try_lock (parker) || wait_block->request < 0) {
					st_parker_unpark (parker, wait_block->wait_key);
				} else {
					if (try_set_used || mutant->state == 1 || !InterlockedCompareExchange (&mutant->state, 1, 0) == 0) {
						try_set = TRUE;
                  try_set_used = FALSE;
               }

               entry->flink = entry;
				}
         } else {
            break;
         }            
      }
        
      /*
		 * It seems that no more waiters can be released, so try to unlock the 
		 * mutant's queue. The unlock fails if there are new waiters.
       */

      if (!locked_queue_try_unlock (&mutant->queue, (mutant->state == 0 && !try_set))) {
			continue;
      }

		if (try_set && mutant->state == 0 && InterlockedCompareExchange (&mutant->state, 1, 0) == 0) {
         try_set = FALSE;
         try_set_used = TRUE;
      }
    } while (st_mutant_is_release_pending (mutant));

    return !try_set_used;
}

static inline void
unlink_list_entry (StSynchronizationEvent *mutant, ListEntry *entry)
{
   if (entry->flink != entry && locked_queue_lock (&mutant->queue, entry)) {
		if (entry->flink != entry) {
			st_list_remove_entry (entry);
		}
		st_mutant_release_waiters_and_unlock_queue (mutant, FALSE);
   }
}

static guint32
enqueue_waiter (StMutant *mutant, WaitBlock *wait_block) 
{
	gboolean first;

   if (!locked_queue_enqueue (&mutant->queue, &wait_block, &first)) {
      return first;
   }
	
	if (!first || mutant->state == 0) {
		locked_queue_try_unlock (&mutant->queue, TRUE);
		if (!st_mutant_is_release_pending (mutant)) {
			return first;
		}
	}

	/*
	 * There are waiters that can be freed (including ourselves).
	 */

	st_mutant_release_waiters_and_unlock_queue (mutant, FALSE);
   return first;
}

guint32 st_mutant_slow_wait (StMutant *mutant, guint32 timeout, 
									  StAlerter *alerter, gboolean interruptible)
{
   StParker parker;
	WaitBlock wait_block;
	guint32 spin_count;
	guint32 wait_status;

	st_parker_init (&parker, 1);
	st_wait_block_init (&wait_block, &parker, ACQUIRE, WAIT_SUCCESS);

	spin_count = enqueue_waiter (mutant, &wait_block) ? mutant->queue.spin_count : 0;
	wait_status = st_parker_park_ex (&parker, spin_count, timeout, alerter, interruptible);

   if (wait_status == WAIT_SUCCESS) {
      return WAIT_SUCCESS;
   }
    
   unlink_list_entry (mutant, &wait_block.wait_list_entry);
   return wait_status;
}

void 
st_mutant_enqueue_locked (StMutant *mutant, WaitBlock *wait_block)
{
	wait_block->request != LOCKED_REQUEST;
   enqueue_waiter (mutant, wait_block);
}