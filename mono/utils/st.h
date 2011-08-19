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
#include <mono/utils/st-ds.h>

typedef struct _SpinWait SpinWait;
typedef struct _StParker StParker;
typedef struct _StAlerter StAlerter;
typedef struct _WaitBlock WaitBlock;
typedef struct _StLock StLock;

#define WAIT_SUCCESS	WAIT_OBJECT_0
#define WAIT_ALERTED	257

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
 * Parker API.
 */

#define WAIT_IN_PROGRESS_BIT	31
#define WAIT_IN_PROGRESS		(1 << WAIT_IN_PROGRESS_BIT)
#define LOCK_COUNT_MASK			((1 << 16) - 1)

struct _StParker {
   struct _StParker * volatile next;
   volatile gint32 state;
   ParkSpot *ps;
   volatile gint32 wait_status;
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
st_parker_unpark_in_progress (StParker *parker, gint32 wait_status) 
{
	parker->wait_status = wait_status;
	return (parker->state & WAIT_IN_PROGRESS) != 0 && 
	       (InterlockedExchange (&parker->state, 0) & WAIT_IN_PROGRESS) != 0;
}

static inline void 
st_parker_unpark_self (StParker *parker, gint32 wait_status)
{
	parker->wait_status = wait_status;
	parker->state = 0;
}

static inline void 
st_parker_unpark (StParker *parker, gint32 wait_status) 
{
	if (!st_parker_unpark_in_progress (parker, wait_status)) {
		ves_icall_System_Threading_StInternalMethods_Set_internal (parker->ps); 
	}
}

guint32 st_parker_park_ex (StParker *parker, gint32 spin_count, 
									gint32 timeout, StAlerter *alerter) MONO_INTERNAL;

static inline guint32 
st_parker_park (StParker *parker)
{
	return st_parker_park_ex (parker, 0, INFINITE, NULL);
}

/*
 * Alerter API.
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
 * Wait block used to queue acquire requets on the synchronizers.
 */

struct _WaitBlock {
   ListEntry wait_list_entry;
   StParker *parker;
	gint32 wait_key;
};

static inline void
st_wait_block_init (WaitBlock *wb, StParker *parker, gint32 wait_key)
{
	wb->parker = parker;
	wb->wait_key = wait_key;
}

/*
 * Non-fair Lock API.
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

#endif // _MONO_ST_H_
