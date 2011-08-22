/* 
 * st.h: Declarations for the SlimThreading synchronizers.
 *
 * Author: 
 * Duarte Nunes (duarte.m.nunes@gmail.com)
 */

#ifndef _MONO_ST_H_
#define _MONO_ST_H_

#include <glib.h>
#include <mono/io-layer/io-layer.h>
#include <mono/metadata/parkspot.h>
#include <mono/utils/mono-compiler.h>
#include <mono/utils/mono-threads.h>
#include <mono/utils/st-ds.h>

typedef struct _SpinWait SpinWait;
typedef struct _StParker StParker;
typedef struct _StAlerter StAlerter;
typedef struct _WaitBlock WaitBlock;
typedef struct _StNotificationEvent StNotificationEvent;
typedef struct _StLock StLock;
typedef struct _LockedQueue LockedQueue;
typedef struct _StMutant StMutant;
typedef struct _StMutant StSynchronizationEvent;
typedef struct _StMutant StFairLock;
typedef struct _StReentrantFairLock StReentrantFairLock;

#define WAIT_SUCCESS			WAIT_OBJECT_0
#define WAIT_ALERTED			257
#define WAIT_INTERRUPTED	512

gboolean st_is_multi_processor (void) MONO_INTERNAL;

/*
 * Spin wait.
 */

struct _SpinWait {
    guint32 count;
};

static inline void
st_spin_wait_init (SpinWait *spinner)
{
	spinner->count = 0;
}

void st_spin_wait (guint32 iterations) MONO_INTERNAL;
void st_spin_once (SpinWait *spinner) MONO_INTERNAL;

/*
 * Parker.
 */

#define WAIT_IN_PROGRESS_BIT	31
#define WAIT_IN_PROGRESS		(1 << WAIT_IN_PROGRESS_BIT)
#define LOCK_COUNT_MASK			((1 << 16) - 1)

struct _StParker {
   struct _StParker * volatile next;
   volatile gint32 state;
   ParkSpot *ps;
   volatile guint32 wait_status;
};	

static inline void 
st_parker_init (StParker *parker, guint16 count) 
{
	parker->state = count | WAIT_IN_PROGRESS;
}

static inline gboolean 
st_parker_is_locked (StParker *parker) 
{
	return (parker->state & LOCK_COUNT_MASK) == 0;
}

static inline gboolean 
st_parker_try_lock (StParker *parker) 
{
	do {
		gint32 state;
		
		if (((state = parker->state) & LOCK_COUNT_MASK) == 0) {
			return FALSE;
		}

		if (InterlockedCompareExchange (&parker->state, state - 1, state) == state) {
			return (state & LOCK_COUNT_MASK) == 1; 
		}
	} while (TRUE);
}

static inline gboolean 
st_parker_try_cancel (StParker *parker) 
{
	do {
		gint32 state;
		
		if (((state = parker->state) & LOCK_COUNT_MASK) == 0) {
			return FALSE;
		}

		if (InterlockedCompareExchange (&parker->state, state & WAIT_IN_PROGRESS, state) == state) {
			return TRUE; 
		}
	} while (TRUE);
}

static inline gboolean 
st_parker_unpark_in_progress (StParker *parker, guint32 wait_status) 
{
	parker->wait_status = wait_status;
	return (parker->state & WAIT_IN_PROGRESS) != 0 && 
	       (InterlockedExchange (&parker->state, 0) & WAIT_IN_PROGRESS) != 0;
}

static inline void 
st_parker_unpark_self (StParker *parker, guint32 wait_status)
{
	parker->wait_status = wait_status;
	parker->state = 0;
}

static inline void 
st_parker_unpark (StParker *parker, guint32 wait_status) 
{
	if (!st_parker_unpark_in_progress (parker, wait_status)) {
		ves_icall_System_Threading_StInternalMethods_Set_internal (parker->ps); 
	}
}

guint32 st_parker_park_ex (StParker *parker, guint32 spin_count, gint32 timeout, 
									StAlerter *alerter, gboolean interruptible) MONO_INTERNAL;

static inline guint32 
st_parker_park (StParker *parker)
{
	return st_parker_park_ex (parker, 0, INFINITE, NULL, FALSE);
}

/*
 * Alerter.
 */

#define ALERTED ((StParker *)~0)

struct _StAlerter {
	StParker * volatile state;
};

static inline void
st_alerter_init (StAlerter *alerter) 
{
	alerter->state = NULL;
}

void st_alerter_alert_parker_list (StParker *first) MONO_INTERNAL;

static inline gboolean 
st_alerter_is_set (StAlerter *alerter) 
{
	return alerter->state == ALERTED;
}

static inline gboolean 
st_alerter_set (StAlerter *alerter) 
{
	StParker *state;

   do {
		if ((state = alerter->state) == ALERTED) {
			return FALSE;
		}

		if (InterlockedCompareExchangePointer ((gpointer *)&alerter->state, ALERTED, state) == state) {
			st_alerter_alert_parker_list (state);
			return TRUE;
		}
   } while (TRUE);
}

static inline gboolean
st_alerter_register_parker (StAlerter *alerter, StParker *parker)
{
   StParker *state;

   do {
		if ((state = alerter->state) == ALERTED) {
			return FALSE;
		}	

		parker->next = state;
		if (InterlockedCompareExchangePointer ((gpointer *)&alerter->state, parker, state) == state) {			
			return TRUE;
		}
   } while (TRUE);
}

void st_alerter_slow_deregister_parker (StAlerter *alerter, StParker *parker) MONO_INTERNAL;

static inline void
st_alerter_deregister_parker (StAlerter *alerter, StParker *parker)
{
    if (parker->next == parker || (parker->next == NULL && 
			InterlockedCompareExchangePointer ((gpointer *)&alerter->state, NULL, parker) == parker)) {
        return;
    }

    st_alerter_slow_deregister_parker(alerter, parker);
}

/*
 * Wait block used to queue acquire requests on the synchronizers.
 */

#define LOCKED_REQUEST	(1 << 31)
#define SPECIAL_REQUEST (1 << 30)
#define MAX_REQUEST		(SPECIAL_REQUEST - 1)

struct _WaitBlock {
   ListEntry wait_list_entry;
   StParker *parker;
	gint32 request;
	gint32 wait_key;
};

static inline void
st_wait_block_init (WaitBlock *wb, StParker *parker, gint32 request, gint32 wait_key)
{
	wb->parker = parker;
	wb->request = request;
	wb->wait_key = wait_key;
}

/*
 * Notification event.
 */

struct _StNotificationEvent {
   volatile ListEntry *state;
   guint32 spin_count;
};

typedef union _EventState {
   struct {	
		volatile guint32 lock : 1;
		volatile guint32 set : 1;
   };
   volatile guint32 bits : 2;
   volatile ListEntry *state;
} EventState;

#define RESET_LOCKED	((ListEntry *)(1 << 0))
#define SET				((ListEntry *)(1 << 1))

static inline void
st_notification_event_init (StNotificationEvent *event, gboolean initialState, guint32 spin_count)
{
   event->state = initialState ? SET : NULL;
	event->spin_count = st_is_multi_processor () ? spin_count : 0;
}

guint32 st_notification_event_slow_wait (StNotificationEvent *event, guint32 timeout, 
													  StAlerter *alerter, gboolean interruptible) MONO_INTERNAL;

static inline guint32
st_notification_event_wait_ex (StNotificationEvent *event, guint32 timeout, 
										 StAlerter *alerter, gboolean interruptible)
{
   EventState state;
   state.state = event->state;

	return state.set != 0 ? WAIT_SUCCESS
		  : timeout == 0 ? WAIT_TIMEOUT 
		  : st_notification_event_slow_wait (event, timeout, alerter, interruptible) == WAIT_SUCCESS;
}

static inline gboolean
st_notification_event_wait (StNotificationEvent *event)
{
   EventState state;
   state.state = event->state;

	return state.set != 0 ? WAIT_SUCCESS 
		  : st_notification_event_slow_wait (event, INFINITE, NULL, FALSE) == WAIT_SUCCESS;
}

gboolean st_notification_event_set (StNotificationEvent *event) MONO_INTERNAL;
gboolean st_notification_event_reset (StNotificationEvent *event) MONO_INTERNAL;
gboolean st_notification_event_is_set (StNotificationEvent *event) MONO_INTERNAL;

/*
 * Non-fair lock.
 */

#define LOCK_FREE	((ListEntry *)~0)
#define LOCK_BUSY	((ListEntry *)0)

struct _StLock {
   volatile ListEntry *state;
   guint32 spin_count;
};

static inline void
st_lock_init (StLock *lock, guint32 spin_count)
{
	lock->state = LOCK_FREE;
	lock->spin_count = st_is_multi_processor () ? spin_count : 0;
}

gboolean st_lock_slow_enter (StLock *lock, guint32 timeout) MONO_INTERNAL;

static inline gboolean
st_lock_try_enter (StLock *lock)
{
	return lock->state == LOCK_FREE && 
			 InterlockedCompareExchangePointer ((gpointer *)&lock->state, LOCK_BUSY, LOCK_FREE) == LOCK_FREE;
}

static inline gboolean
st_lock_enter_ex (StLock *lock, guint32 timeout)
{
	return st_lock_try_enter (lock) || 
		    (timeout != 0 && st_lock_slow_enter (lock, INFINITE));
}

static inline void
st_lock_enter (StLock *lock)
{
	if (!st_lock_try_enter (lock)) {
		st_lock_slow_enter (lock, INFINITE);
	}
}

void st_lock_exit (StLock *lock) MONO_INTERNAL;

/*
 * Locked queue (helper type)
 */

struct _LockedQueue {
   volatile ListEntry *lock_state;
   ListEntry *lock_private_queue;
   ListEntry head;
   volatile gint32 front_request;
   guint32 spin_count;
};

static inline void
locked_queue_init (LockedQueue *queue, guint32 spin_count)
{
	queue->lock_state = LOCK_FREE;
	st_list_initialize (&queue->head);
	queue->front_request = 0;
	queue->spin_count = st_is_multi_processor () ? spin_count : 0;
}

static inline gboolean
locked_queue_is_empty (LockedQueue *queue)
{
	ListEntry *state = queue->lock_state;
	return (state == LOCK_BUSY || state == LOCK_FREE) && st_list_is_empty (&queue->head);
}

static inline gboolean 
locked_queue_try_lock (LockedQueue *queue)
{
	if (queue->lock_state == LOCK_FREE && 
		 InterlockedCompareExchangePointer ((gpointer *)&queue->lock_state, LOCK_BUSY, LOCK_FREE) == LOCK_FREE) {
		queue->front_request = 0;
      queue->lock_private_queue = NULL;
      return TRUE;
   }
   return FALSE;
}

gboolean locked_queue_lock (LockedQueue *queue, ListEntry *entry) MONO_INTERNAL;
gboolean locked_queue_try_unlock (LockedQueue *queue, gboolean force) MONO_INTERNAL;
gboolean locked_queue_enqueue (LockedQueue *queue, WaitBlock *wait_block, gboolean *first) MONO_INTERNAL;

/*
 * Mutant
 */

struct _StMutant {
    volatile gint32 state;
    LockedQueue queue;
};

static inline void
st_mutant_init (StMutant *mutant, gboolean initialState, guint32 spin_count)
{
	locked_queue_init (&mutant->queue, spin_count);
   mutant->state = initialState ? 1 : 0;
}

static inline gboolean
st_mutant_try_acquire (StMutant *mutant)
{
	do {
		if (mutant->state == 0 || !locked_queue_is_empty (&mutant->queue)) {
			return FALSE;
		}

		if (InterlockedCompareExchange (&mutant->state, 0, 1) == 1) {
			return TRUE;
		}
	} while (TRUE);
}

static inline gboolean
st_mutant_is_release_pending (StMutant *mutant)
{
	return mutant->queue.front_request != 0 && mutant->state != 0 && 
			 locked_queue_try_lock (&mutant->queue);
}

void st_mutant_enqueue_locked (StMutant *mutant, WaitBlock *wait_block);

gboolean st_mutant_release_waiters_and_unlock_queue (StMutant *mutant, gboolean try_set) MONO_INTERNAL;

guint32 st_mutant_slow_wait (StMutant *mutant, guint32 timeout, 
									  StAlerter *alerter, gboolean interruptible) MONO_INTERNAL;

static inline guint32
st_mutant_wait (StMutant *mutant, guint32 timeout, StAlerter *alerter, gboolean interruptible)
{
	return st_mutant_try_acquire (mutant) ? WAIT_SUCCESS
		  : timeout == 0 ? WAIT_TIMEOUT
		  : st_mutant_slow_wait (mutant, timeout, alerter, interruptible);
}

/*
 * Synchronization Event
 */

static inline void
st_synchronization_event_init (StSynchronizationEvent *event, gboolean initialState, guint32 spin_count)
{
	st_mutant_init (event, initialState, spin_count);
}

static inline guint32
st_synchronization_event_try_wait_ex (StSynchronizationEvent *event, guint32 timeout, 
												  StAlerter *alerter, gboolean interruptible)
{
	return st_mutant_wait (event, timeout, alerter, FALSE);
}

static inline gboolean 
st_synchronization_event_try_wait (StSynchronizationEvent *event)
{
	return st_mutant_try_acquire (event);
}

static inline gboolean 
st_synchronization_event_wait (StSynchronizationEvent *event)
{
	return st_mutant_wait (event, INFINITE, NULL, FALSE) == WAIT_SUCCESS;
}

static inline gboolean
st_synchronization_event_reset (StSynchronizationEvent *event)
{    
	gint32 state;
    
   if ((state = event->state) != 0) {
		event->state = 0;
   }
   return state != 0;
}

static inline gboolean
st_synchronization_event_set (StSynchronizationEvent *event)
{
	if (event->state == 0 && InterlockedExchange (&event->state, 1) == 0) {  
		if (st_mutant_is_release_pending (event)) {
			st_mutant_release_waiters_and_unlock_queue (event, FALSE);
		}

		return FALSE;
   }

   locked_queue_lock (&event->queue, NULL);
   return st_mutant_release_waiters_and_unlock_queue (event, TRUE);
}

static inline gboolean
st_synchronization_even_is_set (StSynchronizationEvent *event)
{
	return event->state != 0;
}

/*
 * Fair Lock
 */

static inline void
st_fair_lock_init (StFairLock *lock, gboolean initially_owned, guint32 spin_count)
{
	st_mutant_init ((StMutant *)lock, !initially_owned, spin_count);
}

static inline guint32
st_fair_lock_try_enter_ex (StFairLock *lock, guint32 timeout, StAlerter *alerter, gboolean interruptible)
{
	return st_mutant_wait (lock, timeout, alerter, interruptible);
}

static inline gboolean 
st_fair_lock_try_enter (StFairLock *lock)
{
	return st_mutant_try_acquire (lock);
}

static inline gboolean 
st_fair_lock_enter (StFairLock *lock)
{
	return st_mutant_wait (lock, INFINITE, NULL, FALSE) == WAIT_SUCCESS;
}

static inline void
st_fair_lock_exit (StFairLock *lock)
{
	InterlockedExchange (&lock->state, 1);
   if (st_mutant_is_release_pending (lock)) {
      st_mutant_release_waiters_and_unlock_queue (lock, FALSE);
   }
}

/*
 * Reentrant Fair Lock
 */

struct _StReentrantFairLock {
    StFairLock lock;
	 /* 
	  * Use volatile to prevent the compiler from using owner as scratch 
	  * storage, specifically in embedded systems 
	  */
    volatile guint32 owner; 
    guint32 nest;
};

static inline void
st_reentrant_fair_lock_init (StReentrantFairLock *rlock, gboolean initially_owned, guint32 spin_count) 
{
	st_fair_lock_init (&rlock->lock, initially_owned, spin_count);
	rlock->owner = initially_owned ? mono_thread_info_get_small_id () : 0;
	rlock->nest = 0;
}

guint32 st_reentrant_fair_lock_try_enter_ex (StReentrantFairLock *rlock, guint32 timeout, 
															StAlerter *alerter, gboolean interruptible) MONO_INTERNAL;
gboolean st_reentrant_fair_lock_try_enter (StReentrantFairLock *rlock) MONO_INTERNAL;

static inline gboolean 
st_reentrant_fair_lock_enter (StReentrantFairLock *rlock)
{
	return st_reentrant_fair_lock_try_enter_ex (rlock, INFINITE, NULL, FALSE) == WAIT_SUCCESS;
}

static inline void 
st_reentrant_fair_lock_exit (StReentrantFairLock *rlock)
{
	g_assert (rlock->owner != mono_thread_info_get_small_id ());

	if (rlock->nest == 0) {
		rlock->owner = 0;
		st_fair_lock_exit (&rlock->lock);
	} else {
		--rlock->owner;
	}
}

static inline gboolean
st_reentrant_fair_lock_is_owned (StReentrantFairLock *rlock)
{
	return rlock->lock.state == mono_thread_info_get_small_id ();
}

static inline guint32 
st_reentrant_fair_lock_exit_completly (StReentrantFairLock *rlock) 
{
	guint32 nest;
	g_assert (rlock->owner != mono_thread_info_get_small_id ());
	
	nest = rlock->nest;
	rlock->nest = 0;
	rlock->owner = 0;
	st_fair_lock_exit (&rlock->lock);
	return nest;
}

/*
 * The lock is owned by the current thread.
 */

static inline void
st_reentrant_fair_lock_enqueue_locked (StReentrantFairLock *rlock, WaitBlock *wait_block) 
{
	st_mutant_enqueue_locked (&rlock->lock, wait_block);
}

#endif // _MONO_ST_H_
