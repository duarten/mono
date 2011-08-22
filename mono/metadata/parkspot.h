/* 
 * parkspot.h: Declarations for the SlimThreading synchronizers.
 *
 * Author: 
 * Duarte Nunes (duarte.m.nunes@gmail.com)
 */

#ifndef _MONO_PARK_SPOT_H_
#define _MONO_PARK_SPOT_H_

#include <glib.h>
#include <mono/metadata/object-internals.h>
#include <mono/utils/mono-compiler.h>
#include <mono/utils/mono-semaphore.h>

 /*
  * A list of park spots is used because:
  *   1) A wait can be intercepted by a SynchronizationContext;
  *   2) A thread may block when handling a signal, inside the VM.
  * Although there is no MsgWaitForMultipleObjects stuff to deal with,
  * the above reasons may lead to a thread waiting on more than one place.
  * We allow unlimited reentrancy, which we expect not to be a problem.
  */

typedef struct _ParkSpot {
	struct _ParkSpot *next;
	volatile gint32 state;
	struct _MonoInternalThread *thread;
	MonoSemType handle;
} ParkSpot;

void ves_icall_System_Threading_StInternalMethods_Alloc_internal (ParkSpot **ps) MONO_INTERNAL;
void ves_icall_System_Threading_StInternalMethods_Free_internal (ParkSpot *ps) MONO_INTERNAL;
void ves_icall_System_Threading_StInternalMethods_Set_internal (ParkSpot *ps) MONO_INTERNAL;
gboolean ves_icall_System_Threading_StInternalMethods_WaitForParkSpot_internal (ParkSpot *ps, gint32 timeout) MONO_INTERNAL;
gboolean ves_icall_System_Threading_StInternalMethods_Wait_internal (HANDLE handle, gint32 timeout) MONO_INTERNAL;
gint32 ves_icall_System_Threading_StInternalMethods_WaitMultiple_internal (ParkSpot *ps, MonoArray *safe_handles, MonoBoolean waitAll, gint32 timeout) MONO_INTERNAL;

guint32 wait_for_park_spot (ParkSpot *ps, guint32 timeout, gboolean interruptible, gboolean managed) MONO_INTERNAL;

#endif // _MONO_PARK_SPOT_H_
