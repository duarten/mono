/*
 * st-spinwait.c: Spin waiting definitions.
 *
 * Author:
 *	Duarte Nunes (duarte.m.nunes@gmail.com)
 */

#include <mono/utils/st.h>
#define YIELD_FREQUENCY	4000

/*
 * FIXME: Move to mono-threads.h?
 */

static inline void
mono_thread_yield (void) 
{
#ifdef HOST_WIN32
	SwitchToThread ();
#else
	sched_yield ();
#endif
}

gboolean
st_is_multi_processor (void)
{
	static gint32 num_procs = 0;

	if (num_procs == 0) {
		SYSTEM_INFO sys_info;
		GetSystemInfo (&sys_info);
		num_procs = sys_info.dwNumberOfProcessors;
	}

	return num_procs > 1;
}

void
st_spin_wait (guint32 iterations)
{
	while (iterations-- > 0)
	{
	    mono_thread_yield ();
	} 
}

void
st_spin_once (SpinWait *spinner) 
{
    gint32 count;

    count = ++spinner->count & ~(1 << 31);
    if (st_is_multi_processor ()) {
        gint32 remainder = count % YIELD_FREQUENCY;
        if (remainder > 0) {
            st_spin_wait ((gint32)(1.0f + (remainder * 0.032f)));
        } else {
            mono_thread_yield ();
        }
    } else {
        mono_thread_yield ();
    }
}
