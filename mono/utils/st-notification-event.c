/* 
 * st-notification-event.c: Notification event.
 *
 * Author: 
 * Duarte Nunes (duarte.m.nunes@gmail.com)
 */

#include <mono/utils/st.h>

static inline void
unpark_list_entry (ListEntry *entry)
{
   WaitBlock *wait_block = CONTAINING_RECORD (entry, WaitBlock, wait_list_entry);

	if (st_parker_try_lock (wait_block->parker)) {
		st_parker_unpark (wait_block->parker, wait_block->wait_key);
   } else {
      entry->flink = entry;
   }
}

static void
unpark_wait_list (StNotificationEvent *event, ListEntry *list)
{
	ListEntry *next;

   if (list == NULL) {
      return;
   }
    
	/*
	 * If spinning is configured and there is more than one thread in the
	 * wait queue, we first release the thread that is spinning. As only 
	 * one thread spins, we maximize the chances of unparking that thread
	 * before it blocks.
	 */
    
	if (event->spin_count!= 0 && list->flink != NULL) {
      ListEntry *prev = list;

      while ((next = prev->flink) != NULL && next->flink != NULL) {
         prev = next;
      }
      if (next != NULL) {
         prev->flink = NULL;
         unpark_list_entry (next);
      }
   }

   do {
      next = list->flink;
      unpark_list_entry (list);
   } while ((list = next) != NULL);
}

static void
slow_unlink_list_entry (StNotificationEvent *event, ListEntry *entry)
{
   ListEntry *first, *last, *current, *next;
   EventState state, nstate;
   SpinWait spinner;

	st_spin_wait_init (&spinner);
   do {
		state.state = event->state;

		if (entry->flink == entry) {
			return;
		}
	
		if (state.lock == 0 && state.set == 0 && state.state != NULL) {
			if (state.state == entry && entry->flink == NULL) {
				if (InterlockedCompareExchangePointer ((gpointer *)&event->state, NULL, entry) == entry) {
					return;
				}
			} else if (InterlockedCompareExchangePointer ((gpointer *)&event->state, RESET_LOCKED, state.state) == state.state) {
				break;
			}
		}	

		st_spin_once (&spinner);
    } while (TRUE);

	/*
    * Remove all locked entries from the wait list, building a new list with 
	 * the non-locked entries.
    */

   first = last = NULL;
   current = state.state;
   while (current != NULL) {
      next = current->flink;
      if (st_parker_is_locked (CONTAINING_RECORD (current, WaitBlock, wait_list_entry)->parker)) {
			current->flink = current;
      } else {
			if (first == NULL) {
				first = current;
         } else {
				last->flink = current;
         }
			last = current;
		}
		current = next;
   }

	/*
    * Clear the lock bit, returning the non-cancelled wait blocks
	 * to the event's wait list.
    */

	do {
      state.state = event->state;

      if (state.set != 0) {
			event->state = SET;
         state.bits = 0;
         if (first != NULL && state.state != RESET_LOCKED) {
            last->flink = state.state;
         } else {
				first = state.state;
         }
         unpark_wait_list (event, first);
			event->state = SET;
			break;
      } 

		nstate.state = state.state;
		nstate.lock = 0;
		if (first != NULL) {
			last->flink = nstate.state;
			nstate.state = first;
		}
		if (InterlockedCompareExchangePointer ((gpointer *)&event->state, nstate.state, state.state) == state.state) {
				break;
		}        
   } while (TRUE);

	/*
    * It's possible that a thread acquires and releases the lock bit without
	 * finding its wait block in the event's wait list. 
	 */

	while (entry->flink != entry) {
		st_spin_once (&spinner);
	}
   return;
}

static inline void
unlink_list_entry (StNotificationEvent *event, ListEntry *entry)
{
   if (entry->flink == entry || (event->state == entry && entry->flink == NULL && 
		 InterlockedCompareExchangePointer ((gpointer *)&event->state, NULL, entry) == entry)) {
		return;
   }
   slow_unlink_list_entry (event, entry);
}

guint32 
st_notification_event_slow_wait (StNotificationEvent *event, guint32 timeout, 
											StAlerter *alerter, gboolean interruptible)
{
	WaitBlock wait_block;
   StParker parker;
   EventState state, nstate;
   guint32 wait_status;

	st_parker_init (&parker, 1);
	st_wait_block_init (&wait_block, &parker, 0, WAIT_SUCCESS);
   do {

      state.state = event->state;

      if (state.set != 0) {
			return WAIT_SUCCESS;
      }

		wait_block.wait_list_entry.flink = state.state;
		nstate.state = &wait_block.wait_list_entry;
      nstate.lock = state.lock;
      if (InterlockedCompareExchangePointer ((gpointer *)&event->state, nstate.state, state.state) == state.state) {
			break;
      }
   } while (TRUE);

   wait_status = st_parker_park_ex (&parker, state.state == NULL ? event->spin_count : 0, timeout, alerter, interruptible);

   if (wait_status == WAIT_SUCCESS) {
		return WAIT_SUCCESS;
   }
	
   unlink_list_entry (event, &wait_block.wait_list_entry);
   return wait_status;
}

gboolean
st_notification_event_set (StNotificationEvent *event)
{
   EventState state, nstate;

   do {
		state.state = event->state;
		if (state.set != 0) {
			return TRUE;
		}

		if (state.lock != 0) {
			nstate.state = state.state;
			nstate.set = 1;
			if (InterlockedCompareExchangePointer ((gpointer *)&event->state, nstate.state, state.state) == state.state) {
				return FALSE;
			}
		} else if (InterlockedCompareExchangePointer ((gpointer *)&event->state, SET, state.state) == state.state) {
			unpark_wait_list (event, state.state);
			return FALSE;
		}
   } while (TRUE);
}

gboolean
st_notification_event_reset (StNotificationEvent *event)
{
	EventState state;
   SpinWait spinner;

   st_spin_wait_init (&spinner);
   do {
      state.state = event->state;
      if (state.set == 0) {
         return FALSE;
      }

      if (state.lock == 0 &&
			 InterlockedCompareExchangePointer ((gpointer *) &event->state, NULL, SET) == SET) {
			return TRUE;
      }

      st_spin_once(&spinner);
   } while (TRUE);
}

gboolean 
st_notification_event_is_set (StNotificationEvent *event) 
{
   EventState state;
   state.state = event->state;
   return state.set != 0;
}
