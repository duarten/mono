/*
 * st-alerter.c: API for the alerter mechanism, used to cancel a park operation.
 *
 * Author:
 *	Duarte Nunes (duarte.m.nunes@gmail.com)
 */

#include <mono/utils/st.h>

void
st_alerter_alert_parker_list (StParker *first) 
{
	while (first != NULL) {
		StParker *next = first->next;

		if (st_parker_try_cancel (first)) {
			st_parker_unpark (first, WAIT_ALERTED);
		}

		first->next = first;
		first = next;
	}
}

void 
st_alerter_slow_deregister_parker (StAlerter *alerter, StParker *parker)
{
    StParker *state;
    StParker *first;
    StParker *last;
    StParker *next;
    StParker *current;
    SpinWait spinner;

    st_spin_wait_init (&spinner);

retry:
   do {
		if (parker->next == parker) {
         return;
      }

      if ((state = alerter->state) == NULL || state == ALERTED) {
			goto spin_until_unlinked_or_list_changes;
      }

      if (InterlockedCompareExchangePointer ((gpointer *)&alerter->state, NULL, state) == state) {            
			if (state == parker && parker->next == NULL) {
            return;
         }
         break;
      }
   } while (TRUE);

   first = last = NULL;
   current = state;
   do {
      next = current->next;
      
		if (st_parker_is_locked (current)) {
			current->next = current;
      } else {
			if (first == NULL) {
            first = current;
         } else {
            last->next = current;
         }
         last = current;
      }
   } while ((current = next) != NULL);

	/*
    * If we have a non-empty list on hand, we must merge it with
    * the current alerter's list, if the alerter is not set.
	 */

   if (first != NULL) {
      do {
         state = alerter->state;
         
			if (state == ALERTED) {
            last->next = NULL;
            st_alerter_alert_parker_list (first);
            break;
         }

         last->next = state;
         if (InterlockedCompareExchangePointer ((gpointer *)&alerter->state, first, state) == state) {
				state = first;
            break;
         }
      } while (TRUE);
   } else {
      state = NULL;
   }

spin_until_unlinked_or_list_changes:

   while (parker->next != parker) {
      StParker *new_state = alerter->state;
      if (new_state != state && new_state != ALERTED) {
         goto retry;
      }

      st_spin_once (&spinner);
   }
}
