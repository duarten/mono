/* 
 * st-mutant.c: Reentrant fair lock.
 *
 * Author: 
 * Duarte Nunes (duarte.m.nunes@gmail.com)
 */

#include <mono/utils/st.h>

static inline gboolean
try_enter (StReentrantFairLock *rlock, guint32 *tid)
{
	*tid = mono_thread_info_get_small_id ();

   if (st_fair_lock_try_enter (&rlock->lock)) {
      rlock->owner = *tid;
      return TRUE;
   }

   if (rlock->owner == *tid) {
      ++rlock->nest;
      return TRUE;
   }
	
	return FALSE;
}

guint32 
st_reentrant_fair_lock_try_enter_ex (StReentrantFairLock *rlock, guint32 timeout, 
												 StAlerter *alerter, gboolean interruptible) 
{
	guint32 tid;
	guint32 wait_status;

   if (try_enter (rlock, &tid)) {
      return WAIT_SUCCESS;
   }

   if (timeout == 0) {
      return WAIT_TIMEOUT;
   }

   if ((wait_status = st_mutant_slow_wait (&rlock->lock, timeout, alerter, interruptible)) == WAIT_SUCCESS) {
      rlock->owner = tid;
      return WAIT_SUCCESS;
   }
   
	return wait_status;
}

gboolean 
st_reentrant_fair_lock_try_enter (StReentrantFairLock *rlock) 
{
	guint32 tid;	
   return try_enter (rlock, &tid);
}

