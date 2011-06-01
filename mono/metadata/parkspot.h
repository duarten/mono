/*
 * parkspot.h: The park spot API
 *
 * Author:
 *	Duarte Nunes (duarte.m.nunes@gmail.com)
 */

#ifndef _MONO_METADATA_PARKSPOT_H_
#define _MONO_METADATA_PARKSPOT_H_

#include <glib.h>
#include <mono/utils/mono-compiler.h>
#include <mono/utils/mono-semaphore.h>
#include <mono/metadata/object-internals.h>
#include <mono/metadata/threads-types.h>

/*
 * FIXME:
 *  Use specialized park spots depending on the underlying OS and
 *  if shared memory is enabled (e.g., futexes on linux w/o shm).
 */

/*
 * A list of park spots is used because:
 *   1) A wait can be intercepted by a SynchronizationContext;
 *   2) A thread may block when handling an APC or signal, inside the VM.
 * Although there is no MsgWaitForMultipleObjects stuff to deal with,
 * the above reasons may lead to a thread waiting on more than one place.
 * We allow unlimited reentrancy.
 */

typedef struct _ParkSpot {
	struct _ParkSpot *next;
    volatile int state;
    MonoInternalThread *thread;
    MonoSemType handle;
} ParkSpot;

gpointer ves_icall_System_Threading_ParkSpot_Alloc_internal (void) MONO_INTERNAL;
void ves_icall_System_Threading_ParkSpot_Free_internal (ParkSpot *ps) MONO_INTERNAL;
void ves_icall_System_Threading_ParkSpot_Set_internal (ParkSpot *ps) MONO_INTERNAL;
gboolean ves_icall_System_Threading_ParkSpot_Wait_internal (ParkSpot *ps, int timeout) MONO_INTERNAL;

gboolean wait_for_park_spot (ParkSpot *ps, int timeout, gboolean managed) MONO_INTERNAL;

void park_spot_set_os_aware (ParkSpot *ps) MONO_INTERNAL;
gint32 park_spot_wait_os_aware (ParkSpot *ps, int timeout) MONO_INTERNAL;

#endif // _MONO_METADATA_PARKSPOT_H_
