/*
 * parkspot.h: The park spot API
 *
 * Author:
 *	Duarte Nunes (duarte.m.nunes@gmail.com)
 */

#include <mono/metadata/parkspot.h>

void 
park_spot_set_os_aware (ParkSpot *ps) 
{
    mono_sem_post(&ps->handle);
}

gint32 
park_spot_wait_os_aware (ParkSpot *ps, int timeout) 
{
    return mono_sem_timedwait(&ps->handle, timeout, TRUE);
}
