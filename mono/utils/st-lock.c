/*
 * st-lock.c: Non-fair lock.
 *
 * Author:
 *	Duarte Nunes (duarte.m.nunes@gmail.com)
 */

#include <mono/utils/mono-time.h>
#include <mono/utils/st.h>

//
// Unparks the specifed list of waiting threads.
//

static inline void
unpark_wait_list (ListEntry *entry)
{
	 ListEntry *wake_stack;
	 ListEntry *next;
	 WaitBlock *wait_block;

	 if (entry == LOCK_BUSY || entry == LOCK_FREE) {
		  return;
	 }

	 wake_stack = NULL;
	 do {
		  next = entry->flink;
		  wait_block = CONTAINING_RECORD (entry, WaitBlock, wait_list_entry);
		  
		  if (st_parker_try_lock (wait_block->parker)) {			
				entry->blink = wake_stack;
				wake_stack = entry;
		  } else {
				entry->flink = entry;
		  }		
	 } while ((entry = next) != LOCK_BUSY);
	 
	 while (wake_stack != NULL) {
		next = wake_stack->blink;
		wait_block = CONTAINING_RECORD (wake_stack, WaitBlock, wait_list_entry);
		st_parker_unpark (wait_block->parker, wait_block->wait_key);
		wake_stack = next;
	 }
}	

static inline void
unlink_list_entry (StLock *lock, ListEntry *entry)
{
	ListEntry *state;
	SpinWait spinner;

	do {
		if (entry->flink == entry) {
			return;
		}

		if ((state = lock->state) == LOCK_BUSY || state == LOCK_FREE) {
			break;
		}
				
		if (InterlockedCompareExchangePointer ((gpointer *)&lock->state, LOCK_BUSY, state) == state) {
			if (state == entry && entry->flink == LOCK_BUSY) {
				return;
			}

			unpark_wait_list (state);
			break;
		}
	} while (TRUE);
	 
	st_spin_wait_init (&spinner);
	while (entry->flink != entry) {
		st_spin_once (&spinner);
	}
}

gboolean 
st_lock_slow_enter (StLock *lock, guint32 timeout)
{
	 StParker parker;
	 WaitBlock wait_block;
	 ListEntry *state;
	 guint32 last_time;
	 guint32 wait_status;
	 guint32 spin_count;

	 last_time = timeout != INFINITE ? mono_msec_ticks () : 0;

	 st_wait_block_init (&wait_block, &parker, 0, WAIT_SUCCESS);

	 do {
		spin_count = lock->spin_count;
		do {
			if ((state = lock->state) == LOCK_FREE) {
				if (InterlockedCompareExchangePointer ((gpointer *)&lock->state, LOCK_BUSY, LOCK_FREE) == state) {
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
			if ((state = lock->state) == LOCK_FREE) {
				if (InterlockedCompareExchangePointer ((gpointer *)&lock->state, LOCK_BUSY, LOCK_FREE) == LOCK_FREE) {
					return TRUE;
				}
				continue;
			}

			wait_block.wait_list_entry.flink = state;
			if (InterlockedCompareExchangePointer ((gpointer *)&lock->state, &wait_block.wait_list_entry, state) == state) {
				break;
			}
		} while (TRUE);

		wait_status = st_parker_park_ex (&parker, 0, timeout, NULL, FALSE);

		if (wait_status != WAIT_SUCCESS) {
			unlink_list_entry (lock, &wait_block.wait_list_entry);
			return FALSE;
		}

		if (st_lock_try_enter (lock)) {
			return TRUE;
		}

		if (timeout != INFINITE) {
			guint32 now = GetTickCount();
			guint32 elapsed = (now == last_time) ? 1 : (now - last_time);
			if (timeout <= elapsed) {
				return FALSE;
			}
			timeout -= elapsed;
			last_time = now;
		}
	} while (TRUE);
}

void 
st_lock_exit (StLock *lock)
{
	unpark_wait_list ((ListEntry *)InterlockedExchangePointer ((gpointer *)&lock->state, LOCK_FREE));
}
