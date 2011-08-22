/* 
 * st-locked-queue.c: Locked queue.
 *
 * Author: 
 * Duarte Nunes (duarte.m.nunes@gmail.com)
 */

#include <mono/utils/st.h>

#define	LOCK_ONLY_REQUEST	(LOCKED_REQUEST | SPECIAL_REQUEST)

static inline gboolean
process_lock_queue (ListEntry *first, ListEntry *last, ListEntry **lock_queue)
{
	ListEntry *next;
	WaitBlock *wait_block;
	StParker *parker;
	gboolean queue_changed = FALSE;

	do {
		next = first->flink;
		wait_block = CONTAINING_RECORD (first, WaitBlock, wait_list_entry);
		parker = wait_block->parker;

		if (wait_block->request == LOCK_ONLY_REQUEST) {
			first->flink = *lock_queue;
		} else if (!st_parker_is_locked (parker) || wait_block->request < 0) {
			st_list_insert_head (last, first);
			queue_changed = TRUE;
		} else {
			first->flink = first;
		}
	} while ((first = next) != NULL);
	return queue_changed;
}

gboolean 
locked_queue_lock (LockedQueue *queue, ListEntry *entry) 
{
	StParker parker;
	WaitBlock wait_block;
	ListEntry *state; 
	guint32 spin_count;

	st_parker_init (&parker, 1);
	st_wait_block_init (&wait_block, &parker, 0, WAIT_SUCCESS);

	do {		
		spin_count = queue->spin_count;
		do {
			if (entry != NULL && entry->flink == entry) {
				return FALSE;
			}

			if ((state = queue->lock_state) == LOCK_FREE) {
				if (InterlockedCompareExchangePointer ((gpointer *)&queue->lock_state, LOCK_BUSY, LOCK_FREE) == LOCK_FREE) {
					queue->front_request = 0;
					queue->lock_private_queue = NULL;
					return TRUE;
				}
				continue;
			}

			if (state != LOCK_BUSY || spin_count-- == 0) {
				break;
			}

			st_spin_wait (1);
		} while (TRUE);

		st_parker_init (&parker, 1);

		do {
			if (entry != NULL && entry->flink == entry) {
				return FALSE;
			}

			if ((state = queue->lock_state) == LOCK_FREE) {
				if (InterlockedCompareExchangePointer ((gpointer *)&queue->lock_state, LOCK_BUSY, LOCK_FREE) == state) {
					queue->front_request = 0;
					queue->lock_private_queue = NULL;
					return TRUE;
				}
				continue;
			}

			wait_block.wait_list_entry.flink = state;
			if (InterlockedCompareExchangePointer ((gpointer *)&queue->lock_state, &wait_block.wait_list_entry, state) == state) {
				break;
			}
		} while (TRUE);

		st_parker_park (&parker);
	} while (TRUE);
}

gboolean locked_queue_try_unlock (LockedQueue *queue, gboolean force) 
{
	ListEntry *entry, *head = &queue->head;

   if (!force && !st_list_is_empty (head)) {
      force = TRUE;
   }

   do {
		ListEntry *state;
		if ((state = queue->lock_state) == LOCK_BUSY) {
			queue->front_request = (entry = head->flink) == head
										? 0 
										: CONTAINING_RECORD (entry, WaitBlock, wait_list_entry)->request & MAX_REQUEST;

         if (InterlockedCompareExchangePointer ((gpointer *)&queue->lock_state, LOCK_FREE, LOCK_BUSY) == LOCK_BUSY) {
				entry = queue->lock_private_queue;
            while (entry != NULL) {
               ListEntry *next  = entry->flink;
					st_parker_unpark (CONTAINING_RECORD (entry, WaitBlock, wait_list_entry)->parker, WAIT_SUCCESS);
               entry = next;
            }
            return TRUE;
         }

         queue->front_request = 0;
		} else if (InterlockedCompareExchangePointer ((gpointer *)queue->lock_state, NULL, state) == state) {
			if (process_lock_queue (state, head->blink, &queue->lock_private_queue) && !force) {
				return FALSE;
			}
		}
   } while (TRUE);
}

gboolean locked_queue_enqueue (LockedQueue *queue, WaitBlock *wait_block, gboolean *first) 
{
   ListEntry *head = &queue->head;

   do {
		ListEntry *state;

      if ((state = queue->lock_state) == LOCK_FREE) {
			if (InterlockedCompareExchangePointer ((gpointer *)&queue->lock_state, LOCK_BUSY, LOCK_FREE) == LOCK_FREE) {
				st_list_insert_tail (head, &wait_block->wait_list_entry);
				*first = head->flink == &wait_block->wait_list_entry;
				queue->front_request = 0;
				queue->lock_private_queue = NULL;
            return TRUE;
         }
         continue;
      }

      wait_block->wait_list_entry.flink = state;
      if (InterlockedCompareExchangePointer ((gpointer *)&queue->lock_state, &wait_block->wait_list_entry, state) == state) {
         *first = state == NULL && st_list_is_empty (head);
         return FALSE;
      }
   } while (TRUE);
}
