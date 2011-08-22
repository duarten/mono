/* 
 * st-parker.c: The parker object.
 *
 * Author: 
 * Duarte Nunes (duarte.m.nunes@gmail.com)
 */

#include <mono/metadata/parkspot.h>
#include <mono/utils/st.h>
#include <mono/utils/mono-memory-model.h>

guint32 
st_parker_park_ex (StParker *parker, guint32 spin_count, gint32 timeout, 
						 StAlerter *alerter, gboolean interruptible)
{
	gboolean registered;
	guint32 wait_status;

	do {
		if (parker->state >= 0) {
			LOAD_LOAD_FENCE;
			return parker->wait_status;
		}
		if (spin_count-- == 0) {
			break;
		}
		if (alerter != NULL && alerter->state == ALERTED && 
			 st_parker_try_cancel (parker)) {
			return WAIT_ALERTED;
		}
		st_spin_wait (1);
	} while (TRUE);
       
	 ves_icall_System_Threading_StInternalMethods_Alloc_internal (&parker->ps);

	do {
		gint32 state;
		
		if (((state = parker->state) & WAIT_IN_PROGRESS_BIT) == 0) {
			ves_icall_System_Threading_StInternalMethods_Free_internal (parker->ps);
			return parker->wait_status;
		}

		if (InterlockedCompareExchange (&parker->state, state & ~WAIT_IN_PROGRESS, state) == state) {
			return TRUE; 
		}
	} while (TRUE);

   registered = FALSE;
   if (alerter != NULL && !(registered = st_alerter_register_parker (alerter, parker))) {						
		if (st_parker_try_cancel (parker)) {
			ves_icall_System_Threading_StInternalMethods_Free_internal (parker->ps);
			return WAIT_ALERTED;
		}

		timeout = INFINITE;
   }

	if ((wait_status = wait_for_park_spot (parker->ps, timeout, interruptible, FALSE)) != 1) {
      if (st_parker_try_cancel (parker)) {
         st_parker_unpark_self (parker, wait_status == 0 ? WAIT_TIMEOUT : WAIT_INTERRUPTED);
      } else if (wait_for_park_spot (parker->ps, INFINITE, FALSE, FALSE) != 1) {
			st_parker_unpark_self (parker, WAIT_INTERRUPTED);
      }
   }
   
   if (registered) {
      st_alerter_deregister_parker (alerter, parker);
   }
   
	ves_icall_System_Threading_StInternalMethods_Free_internal (parker->ps);
   return parker->wait_status;
}
