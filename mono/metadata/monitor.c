/*
 * monitor.c:  Monitor locking functions
 *
 * Author:
 *		Dick Porter (dick@ximian.com)
 *  	Duarte Nunes (duarte.m.nunes@gmail.com)
 *
 * Copyright 2003 Ximian, Inc (http://www.ximian.com)
 * Copyright 2004-2009 Novell, Inc (http://www.novell.com)
 */

#include <config.h>
#include <glib.h>
#include <string.h>

#include <mono/io-layer/io-layer.h>
#include <mono/metadata/monitor.h>
#include <mono/metadata/threads-types.h>
#include <mono/metadata/exception.h>
#include <mono/metadata/threads.h>
#include <mono/metadata/object-internals.h>
#include <mono/metadata/class-internals.h>
#include <mono/metadata/gc-internal.h>
#include <mono/metadata/method-builder.h>
#include <mono/metadata/debug-helpers.h>
#include <mono/metadata/tabledefs.h>
#include <mono/metadata/marshal.h>
#include <mono/metadata/profiler-private.h>
#include <mono/utils/mono-time.h>
#include <mono/utils/mono-threads.h>
#include <mono/utils/mono-memory-model.h>
#include <mono/utils/st.h>

/*
 * Pull the list of opcodes
 */
#define OPDEF(a,b,c,d,e,f,g,h,i,j) \
	a = i,

enum {
#include "mono/cil/opcode.def"
	LAST = 0xff
};
#undef OPDEF

/* #define LOCK_DEBUG(a) do { a; } while (0) */
#define LOCK_DEBUG(a)

/*
 * The monitor implementation here is based on
 * http://www.research.ibm.com/people/d/dfb/papers/Bacon98Thin.ps and
 * http://www.research.ibm.com/trl/projects/jit/paper/oopsla99_onodera.pdf
 * 
 * Bacon's thin locks have a fast path that doesn't need a lock record
 * for the common case of locking an unlocked or shallow-nested object.
 * When the fast path fails it means there is contention (or the header
 * contains the object's hash code) and so the thread must inflate the
 * monitor by creating a lock record and placing its address in the
 * synchronization header of the object once the lock is free.
 *
 * To inflate the monitor we use a temporary mapping from the MonoObject,
 * which is pinned, to the MonoThreadsSync that the object will point to.
 * This table is only required during inflation and allows the acquiring 
 * threads to block waiting for the lock to be released.
 */

/*
 * Lock word format:
 *
 * 32-bit
 *		LOCK_WORD_FLAT:			[owner:22 | nest:8 | status:2]
 *    LOCK_WORD_THIN_HASH:	   [      hash:30     | status:2]
 *		LOCK_WORD_INFLATED:		[    sync_ptr:30   | status:2]
 *
 * 64-bit
 *		LOCK_WORD_FLAT:			[unused:22 | owner:32 | nest:8 | status:2]
 *    LOCK_WORD_THIN_HASH:	   [            hash:62           | status:2]
 *		LOCK_WORD_INFLATED:		[          sync_ptr:62         | status:2]
 *
 * We assume that the two least significant bits of a MonoThreadsSync * are always zero.
 */

typedef union {
	gsize lock_word;
	volatile MonoThreadsSync *sync; /* *volatile* qualifier used just to remove some warnings. */
} LockWord;

enum {
	LOCK_WORD_FLAT = 0,
	LOCK_WORD_THIN_HASH = 1,
	LOCK_WORD_INFLATED = 2,
	LOCK_WORD_FAT_HASH = 3,
	
	LOCK_WORD_STATUS_BITS = 2,
	LOCK_WORD_BITS_MASK = (1 << LOCK_WORD_STATUS_BITS) - 1,
	
	LOCK_WORD_NEST_BITS = 8,
	LOCK_WORD_NEST_SHIFT = LOCK_WORD_STATUS_BITS,
   LOCK_WORD_NEST_MASK = ((1 << LOCK_WORD_NEST_BITS) - 1) << LOCK_WORD_NEST_SHIFT,
	
	LOCK_WORD_OWNER_SHIFT = LOCK_WORD_NEST_SHIFT + LOCK_WORD_NEST_BITS,

	LOCK_WORD_HASH_SHIFT = LOCK_WORD_STATUS_BITS
};

#define MONITOR_SPIN_COUNT 256

struct _MonoThreadsSync
{
	StReentrantFairLock lock;
	ListEntry wait_list;
#ifdef HAVE_MOVING_COLLECTOR
	gint32 hash_code;
#endif
	void *data;
};

typedef struct _MonitorArray MonitorArray;

struct _MonitorArray {
	MonitorArray *next;
	int num_monitors;
	MonoThreadsSync monitors [MONO_ZERO_LEN_ARRAY];
};

#define mono_monitor_allocator_lock() EnterCriticalSection (&monitor_mutex)
#define mono_monitor_allocator_unlock() LeaveCriticalSection (&monitor_mutex)
static CRITICAL_SECTION monitor_mutex;
static GHashTable *monitor_table;
static MonoThreadsSync *monitor_freelist;
static MonitorArray *monitor_allocated;
static int array_size = 16;

#define MONO_OBJECT_ALIGNMENT_SHIFT	3

void
mono_monitor_init (void)
{
	InitializeCriticalSection (&monitor_mutex);
	monitor_table = g_hash_table_new (NULL, NULL);
}
 
void
mono_monitor_cleanup (void)
{	
	/*MonoThreadsSync *mon;
	  MonitorArray *marray, *next = NULL;*/

	/*DeleteCriticalSection (&monitor_mutex);*/

	/*g_hash_table_destroy (monitor_table);*/

	/* The monitors on the freelist don't have weak links - mark them */
	/*for (mon = monitor_freelist; mon; mon = mon->data)
		mon->wait_list = (gpointer)-1;
	*/
	/* FIXME: This still crashes with sgen (async_read.exe) */
	/*
	for (marray = monitor_allocated; marray; marray = next) {
		int i;

		for (i = 0; i < marray->num_monitors; ++i) {
			mon = &marray->monitors [i];
			if (mon->wait_list != (gpointer)-1)
				mono_gc_weak_link_remove (&mon->data);
		}

		next = marray->next;
		g_free (marray);
	}
	*/
}

/*
 * mono_monitor_init_tls:
 *
 *   Setup TLS variables used by the monitor code for the current thread.
 */
void
mono_monitor_init_tls (void)
{ }

static int
monitor_is_on_freelist (MonoThreadsSync *mon)
{
	MonitorArray *marray;
	for (marray = monitor_allocated; marray; marray = marray->next) {
		if (mon >= marray->monitors && mon < &marray->monitors [marray->num_monitors])
			return TRUE;
	}
	return FALSE;
}

/**
 * mono_locks_dump:
 * @include_untaken:
 *
 * Print a report on stdout of the managed locks currently held by
 * threads. If @include_untaken is specified, list also inflated locks
 * which are unheld.
 * This is supposed to be used in debuggers like gdb.
 */
void
mono_locks_dump (gboolean include_untaken)
{
	int i;
	int used = 0, on_freelist = 0, to_recycle = 0, total = 0, num_arrays = 0;
	MonoThreadsSync *mon;
	MonitorArray *marray;
	for (mon = monitor_freelist; mon; mon = mon->data)
		on_freelist++;
	for (marray = monitor_allocated; marray; marray = marray->next) {
		total += marray->num_monitors;
		num_arrays++;
		for (i = 0; i < marray->num_monitors; ++i) {
			mon = &marray->monitors [i];
			if (mon->data == NULL) {
				if (i < marray->num_monitors - 1)
					to_recycle++;
			} else {
				if (!monitor_is_on_freelist ((MonoThreadsSync *) mon->data)) {
					MonoObject *holder = mono_gc_weak_link_get (&mon->data);
					if (mon->lock.owner) {
						g_print ("Lock %p in object %p held by thread %p, nest level: %d\n",
							mon, holder, (void*)mon->lock.owner, mon->lock.nest);
						if (mon->lock.lock.queue.front_request != 0)
							g_print ("\tThere are threads waiting to acquire the lock\n");
					} else if (include_untaken) {
						g_print ("Lock %p in object %p untaken\n", mon, holder);
					}
					used++;
				}
			}
		}
	}
	g_print ("Total locks (in %d array(s)): %d, used: %d, on freelist: %d, to recycle: %d\n",
		num_arrays, total, used, on_freelist, to_recycle);
}

/* LOCKING: this is called with monitor_mutex held */
static void 
mon_finalize (MonoThreadsSync *mon)
{
	LOCK_DEBUG (g_message ("%s: Finalizing sync %p", __func__, mon));

	mono_gc_weak_link_remove (&mon->data);
	mon->data = monitor_freelist;
	monitor_freelist = mon;
	mono_perfcounters->gc_sync_blocks--;
}

/* LOCKING: this is called with monitor_mutex held */
static MonoThreadsSync *
mon_new ()
{
	MonoThreadsSync *new;

	if (!monitor_freelist) {
		MonitorArray *marray;
		int i;
		/* see if any sync block has been collected */
		new = NULL;
		for (marray = monitor_allocated; marray; marray = marray->next) {
			for (i = 0; i < marray->num_monitors; ++i) {
				if (marray->monitors [i].data == NULL) {
					new = &marray->monitors [i];
					new->data = monitor_freelist;
					monitor_freelist = new;
				}
			}
			/* small perf tweak to avoid scanning all the blocks */
			if (new)
				break;
		}
		/* need to allocate a new array of monitors */
		if (!monitor_freelist) {
			MonitorArray *last;
			LOCK_DEBUG (g_message ("%s: allocating more monitors: %d", __func__, array_size));
			marray = g_malloc0 (sizeof (MonoArray) + array_size * sizeof (MonoThreadsSync));
			marray->num_monitors = array_size;
			array_size *= 2;
			/* link into the freelist */
			for (i = 0; i < marray->num_monitors - 1; ++i) {
				marray->monitors [i].data = &marray->monitors [i + 1];
			}
			marray->monitors [i].data = NULL; /* the last one */
			monitor_freelist = &marray->monitors [0];
			/* we happend the marray instead of prepending so that
			 * the collecting loop above will need to scan smaller arrays first
			 */
			if (!monitor_allocated) {
				monitor_allocated = marray;
			} else {
				last = monitor_allocated;
				while (last->next)
					last = last->next;
				last->next = marray;
			}
		}
	}

	new = monitor_freelist;
	monitor_freelist = new->data;

	st_reentrant_fair_lock_init (&new->lock, TRUE, MONITOR_SPIN_COUNT);
	
	mono_perfcounters->gc_sync_blocks++;
	return new;
}

static inline void
mono_monitor_ensure_synchronized (LockWord lw)
{
	guint32 id = mono_thread_info_get_small_id ();

	if ((lw.lock_word & LOCK_WORD_BITS_MASK) == 0) {
		if ((((unsigned int)lw.lock_word) >> LOCK_WORD_OWNER_SHIFT) == id) {
			return;
		}
	} else if (lw.lock_word & LOCK_WORD_INFLATED) {
		lw.lock_word &= ~LOCK_WORD_BITS_MASK;
		if (lw.sync->lock.owner == id) {
			return;
		}
	}

	LOCK_DEBUG (g_message ("%s: (%d) Synchronization error with lock word %p", __func__, mono_thread_info_get_small_id (), lw.sync));
		
	mono_raise_exception (mono_get_exception_synchronization_lock ("Object synchronization method was called from an unsynchronized block of code."));	
}

/*
 * When this function is called it has already been established that the
 * current thread owns the monitor.
 */
static inline void
mono_monitor_exit_inflated (MonoObject *obj, MonoThreadsSync *mon)
{
	st_reentrant_fair_lock_exit (&mon->lock);
	LOCK_DEBUG (g_message ("%s: (%d) Unlocked %p (owner: %d, nest: %d)", __func__, mono_thread_info_get_small_id (), obj, mon->lock.owner, mon->nest));
}

/*
 * When this function is called it has already been established that the
 * current thread owns the monitor.
 */
static inline void
mono_monitor_exit_flat (MonoObject *obj, LockWord lw)
{
	if (G_UNLIKELY (lw.lock_word & LOCK_WORD_NEST_MASK)) {
		lw.lock_word -= 1 << LOCK_WORD_NEST_SHIFT;
	} else {
		lw.lock_word = 0;
	}
	obj->synchronisation = lw.sync;
	UNLOCK_FENCE;

	LOCK_DEBUG (g_message ("%s: (%d) Unlocked %p (lock word: %p)", __func__, mono_thread_info_get_small_id (), obj, lw.sync));
}

void
mono_monitor_exit (MonoObject *obj)
{
	LockWord lw;
	
	LOCK_DEBUG (g_message ("%s: (%d) Unlocking %p", __func__, mono_thread_info_get_small_id (), obj));

	if (G_UNLIKELY (!obj)) {
		mono_raise_exception (mono_get_exception_argument_null ("obj"));
		return;
	}

	lw.sync = obj->synchronisation;

	mono_monitor_ensure_synchronized (lw);

	if (G_UNLIKELY (lw.lock_word & LOCK_WORD_INFLATED)) {
		lw.lock_word &= ~LOCK_WORD_BITS_MASK;
		mono_monitor_exit_inflated (obj, lw.sync);
	} else {
		mono_monitor_exit_flat (obj, lw);
	}
}

/* 
 * If allow_interruption == TRUE, the method will be interrupted if abort or suspend
 * is requested. In this case it returns -1.
 */
static inline gint32 
mono_monitor_try_enter_inflated (MonoObject *obj, MonoThreadsSync *mon,
											guint32 ms, gboolean allow_interruption)
{	
	guint32 wait_status;

	if (st_reentrant_fair_lock_try_enter (&mon->lock)) {
		return 1;
	}

	mono_perfcounters->thread_contentions++;
		
	if (G_UNLIKELY (ms == 0)) {
		LOCK_DEBUG (g_message ("%s: (%d) timed out, returning FALSE", __func__, mono_thread_info_get_small_id ()));
		return 0;
	}

	mono_profiler_monitor_event (obj, MONO_PROFILER_MONITOR_CONTENTION);
	
	wait_status = st_reentrant_fair_lock_try_enter_ex (&mon->lock, ms, NULL, allow_interruption);

	if (wait_status == WAIT_SUCCESS) {
		return 1;
	}
	
	mono_profiler_monitor_event (obj, MONO_PROFILER_MONITOR_FAIL);

	if (wait_status == WAIT_TIMEOUT) {
		LOCK_DEBUG (g_message ("%s: (%d) timed out waiting, returning FALSE", __func__, mono_thread_info_get_small_id ()));
		return 0;
	}

	LOCK_DEBUG (g_message ("%s: (%d) was interrupted", __func__, mono_thread_info_get_small_id ()));
	return -1;
}

static inline void
fail_inflation (MonoObject *obj, MonoThreadsSync *mon)
{
	gsize nv = (gsize)mon->wait_list.flink - 1;
	mon->wait_list.flink = (ListEntry *)nv;
	if (nv == 0) {
		g_hash_table_remove (monitor_table, obj);
		mon_finalize (mon);
	}
}

/*
 * Returns with the monitor lock held.
 */
static gint32 
mono_monitor_inflate (MonoObject *obj, guint32 ms, gboolean allow_interruption)
{
	LockWord lw;
	MonoThreadsSync *mon;
	gboolean locked;
	guint32 then;
	guint32 ret;

	LOCK_DEBUG (g_message ("%s: (%d) Inflating lock object %p", __func__, mono_thread_info_get_small_id (), obj));

	then = ms != INFINITE ? mono_msec_ticks () : 0;

	/*
	 * Allocate a lock record and register the object in the monitor table.
	 */

retry:
	mono_monitor_allocator_lock ();		
	if ((locked = ((mon = (MonoThreadsSync *)g_hash_table_lookup (monitor_table, obj)) == NULL))) {
		mon = mon_new ();
		g_hash_table_insert (monitor_table, obj, mon); 
		mon->wait_list.flink = (ListEntry *)1;
	} else {
		mon->wait_list.flink = (ListEntry *)((gsize)mon->wait_list.flink + 1);
	}
	mono_monitor_allocator_unlock ();

	if (locked) {
		mono_gc_weak_link_add (&mon->data, obj, FALSE);
	}

	/*
	 * Check if the monitor is already inflated and if we hold the correct one.
	 */

	lw.sync = obj->synchronisation;
	
	if (lw.lock_word & LOCK_WORD_INFLATED) {
		lw.lock_word &= ~LOCK_WORD_STATUS_BITS;
		if (lw.sync != mon) {
			mono_monitor_allocator_lock ();
			fail_inflation (obj, mon);
			mono_monitor_allocator_unlock ();
		}

		return mono_monitor_try_enter_inflated (obj, lw.sync, ms, allow_interruption);
	}

	if (!locked) {
		if ((ret = mono_monitor_try_enter_inflated (obj, mon, ms, allow_interruption)) != 1) {
			mono_monitor_allocator_lock ();

			lw.sync = obj->synchronisation;
	
			if ((lw.lock_word & LOCK_WORD_INFLATED) == 0) {
				fail_inflation (obj, mon);
			}

			mono_monitor_allocator_unlock ();
			LOCK_DEBUG (g_message ("%s: (%d) Failed to inflated lock object %p", __func__, mono_thread_info_get_small_id (), obj));
			return ret;
		}

		lw.sync = obj->synchronisation;
		if (lw.lock_word & LOCK_WORD_INFLATED) {
			return 1;
		}
	}

	/*
	 * Wait for the lock to be released.
	 */

	do {

		/*
		 * Check if the lock can be acquired and build the new lock word. We do
		 * the latter inside the loop as a kind of backoff.
		 */

		if (lw.lock_word == 0 || (lw.lock_word & LOCK_WORD_THIN_HASH) != 0) {
			LockWord nlw;
			nlw.sync = mon;
			nlw.lock_word |= LOCK_WORD_INFLATED;
			
#ifdef HAVE_MOVING_COLLECTOR
			if (lw.lock_word & LOCK_WORD_THIN_HASH) {
				nlw.lock_word |= LOCK_WORD_THIN_HASH;
				mon->hash_code = (unsigned int)lw.lock_word >> LOCK_WORD_HASH_SHIFT;
			}
#endif
			if (InterlockedCompareExchangePointer ((gpointer *)&obj->synchronisation, nlw.sync, lw.sync) == lw.sync) {

				/*
				 * The lock is inflated. Now we can remove the object from the monitor table.
				 */

				mono_monitor_allocator_lock ();
				st_list_initialize (&mon->wait_list);
				g_hash_table_remove (monitor_table, obj);
				mono_monitor_allocator_unlock ();

				LOCK_DEBUG (g_message ("%s: (%d) Inflated lock object %p to mon %p (%d)", __func__, mono_thread_info_get_small_id (), obj, mon, mon->owner));
				return 1;
			}
		}

#ifdef HOST_WIN32
		SwitchToThread ();
#else
		sched_yield ();
#endif

		if (ms != INFINITE) {
			int now = mono_msec_ticks ();
			int elapsed = now == then ? 1 : now - then;
			if (ms <= elapsed) {
				mono_monitor_exit_inflated (obj, mon);
				mono_monitor_allocator_lock ();
				fail_inflation (obj, mon);
				mono_monitor_allocator_unlock ();
				LOCK_DEBUG (g_message ("%s: (%d) Inflation of lock object %p timed out", __func__, mono_thread_info_get_small_id (), obj));
				return 0;
			} else {
				ms -= elapsed;
			}

			then = now;
		}

		if (mono_thread_interruption_requested ()) {
			mono_monitor_exit_inflated (obj, mon);
			mono_monitor_allocator_lock ();
			fail_inflation (obj, mon);
			mono_monitor_allocator_unlock ();

			mono_thread_force_interruption_checkpoint ();
			goto retry;
		}

		lw.sync = obj->synchronisation;
	} while (TRUE);
}

static inline gboolean
mono_monitor_inflate_owned (MonoObject *obj)
{
	LockWord lw;
	guint32 nest;

	LOCK_DEBUG (g_message("%s: (%d) Inflating lock %p owned by the current thread", __func__, mono_thread_info_get_small_id (), obj));

	lw.sync = obj->synchronisation;
	nest = (lw.lock_word & LOCK_WORD_NEST_MASK) >> LOCK_WORD_NEST_SHIFT;
	
	obj->synchronisation = 0;

	/*
	 * If we can't regain ownership of the monitor, then we're shutting down.
	 */
	
	if (mono_monitor_inflate (obj, INFINITE, FALSE) == -1) {
		return FALSE;
	}

	lw.sync = obj->synchronisation;
	lw.lock_word &= ~LOCK_WORD_BITS_MASK;
	lw.sync->lock.nest = nest;

	return TRUE;
}

static inline gint32 
mono_monitor_try_enter_internal (MonoObject *obj, guint32 ms, gboolean allow_interruption)
{
	LockWord lw;
	int id;

	LOCK_DEBUG (g_message("%s: (%d) Trying to lock object %p (%d ms)", __func__, mono_thread_info_get_small_id (), obj, ms));

	if (G_UNLIKELY (!obj)) {
		mono_raise_exception (mono_get_exception_argument_null ("obj"));
	}

	lw.sync = obj->synchronisation;
	id = mono_thread_info_get_small_id ();

	if (G_LIKELY (lw.lock_word == 0)) {
		LockWord nlw;
		nlw.lock_word = id << LOCK_WORD_OWNER_SHIFT;
			
		if (InterlockedCompareExchangePointer ((gpointer *)&obj->synchronisation, nlw.sync, NULL) == NULL) {
			return 1;
		}
		lw.sync = obj->synchronisation;
	} else if ((lw.lock_word & LOCK_WORD_BITS_MASK) == 0 && ((unsigned int)lw.lock_word >> LOCK_WORD_OWNER_SHIFT) == id) {
		if ((lw.lock_word & LOCK_WORD_NEST_MASK) == LOCK_WORD_NEST_MASK) {
			mono_monitor_inflate_owned (obj);
			lw.sync = obj->synchronisation;
			lw.lock_word &= ~LOCK_WORD_BITS_MASK;
			lw.sync->lock.nest += 1;
		} else {
			lw.lock_word += 1 << LOCK_WORD_NEST_SHIFT;
			obj->synchronisation = lw.sync;
		}
		return 1;
	} 

	if (lw.lock_word & LOCK_WORD_INFLATED) {
		lw.lock_word &= ~LOCK_WORD_BITS_MASK;
		return mono_monitor_try_enter_inflated (obj, lw.sync, ms, allow_interruption);
	}

	/*
	 * Either there's contention for the lock or the lock word contains the hash
	 * code. Either way, inflate the monitor.
	 */

	return mono_monitor_inflate (obj, ms, allow_interruption);
}

gboolean 
mono_monitor_enter (MonoObject *obj)
{
	return mono_monitor_try_enter_internal (obj, INFINITE, FALSE) == 1;
}

gboolean 
mono_monitor_try_enter (MonoObject *obj, guint32 ms)
{
	return mono_monitor_try_enter_internal (obj, ms, FALSE) == 1;
}

/*
 * mono_object_hash:
 * @obj: an object
 *
 * Calculate a hash code for @obj that is constant while @obj is alive.
 */
int
mono_object_hash (MonoObject* obj)
{
#ifdef HAVE_MOVING_COLLECTOR
	LockWord lw;
	unsigned int hash;

	if (!obj) {
		return 0;
	}

	lw.sync = obj->synchronisation;
	
	if (lw.lock_word & LOCK_WORD_THIN_HASH) {
		/*g_print ("fast thin hash %d for obj %p store\n", (unsigned int)lw.lock_word >> LOCK_WORD_HASH_SHIFT, obj);*/
		return (unsigned int)lw.lock_word >> LOCK_WORD_HASH_SHIFT;
	}

	if (lw.lock_word & LOCK_WORD_FAT_HASH) {
		lw.lock_word &= ~LOCK_WORD_BITS_MASK;
		/*g_print ("fast fat hash %d for obj %p store\n", lw.sync->hash_code, obj);*/
		return lw.sync->hash_code;
	}

	/*
	 * Compute the 30-bit hash.
	 */

	hash = (GPOINTER_TO_UINT (obj) >> MONO_OBJECT_ALIGNMENT_SHIFT) * 2654435761u;
	hash &= ~(LOCK_WORD_BITS_MASK << 30);

	/*
	 * While we are inside this function, the GC will keep this object pinned,
	 * since we are in the unmanaged stack. Thanks to this and to the hash
	 * function that depends only on the address, we can ignore the races if
	 * another thread computes the hash at the same time, because it'll end up
	 * with the same value.
	 */	
	
	if (lw.lock_word == 0) {
		/*g_print ("storing thin hash code %d for obj %p\n", hash, obj);*/
			
		lw.lock_word = LOCK_WORD_THIN_HASH | (hash << LOCK_WORD_HASH_SHIFT);
		if (InterlockedCompareExchangePointer ((gpointer*)&obj->synchronisation, lw.sync, NULL) == NULL) {
			return hash;
		}

		/*g_print ("failed store\n");*/
			
		lw.sync = obj->synchronisation;

		if (lw.lock_word & LOCK_WORD_THIN_HASH) {
			return hash;
		}

		/* 
		 * Someone acquired or inflated the lock.
		 */
	}	
	 
	if ((lw.lock_word & LOCK_WORD_INFLATED) == 0) {
	
		/*
		 * The lock is owned by some thread, so we must inflate it. Note that it's
		 * not common to both lock an object and ask for its hash code.
		 */

		gboolean locked;
		
		if ((locked = ((unsigned int)lw.lock_word >> LOCK_WORD_OWNER_SHIFT) == mono_thread_info_get_small_id ())) {
			if (!mono_monitor_inflate_owned (obj)) {
				return 0;
			}
		} else if (mono_monitor_inflate (obj, INFINITE, FALSE) == -1) {
			return 0;
		}

		lw.sync = obj->synchronisation;
		lw.lock_word &= ~LOCK_WORD_BITS_MASK;

		if (!locked) {
			mono_monitor_exit_inflated (obj, lw.sync);
		}		
	}

	/*g_print ("storing hash code %d for obj %p in sync %p\n", hash, obj, lw.sync);*/

	lw.sync->hash_code = hash;
	lw.lock_word |= LOCK_WORD_FAT_HASH;

	/* 
	 * This is safe while we don't deflate locks. 
	 */
			
	obj->synchronisation = lw.sync;
	return hash;
#else
	/*
    * Wang's address-based hash function:
	 *   http://www.concentric.net/~Ttwang/tech/addrhash.htm
	 */
	return (GPOINTER_TO_UINT (obj) >> MONO_OBJECT_ALIGNMENT_SHIFT) * 2654435761u;
#endif
}

void**
mono_monitor_get_object_monitor_weak_link (MonoObject *object)
{
	LockWord lw;

	lw.sync = object->synchronisation;

	if (lw.lock_word & LOCK_WORD_INFLATED) {
		lw.lock_word &= ~LOCK_WORD_BITS_MASK;
		return (void **)&lw.sync->data;
	} 

	return NULL;
}

static void
emit_obj_syncp_check (MonoMethodBuilder *mb, int thread_tls_offset, int syncp_add_loc, 
							 int syncp_loc, int tid_loc, int *obj_null_branch, int *thread_info_null_branch)
{
	/*
		ldarg				0															obj
		brfalse.s		obj_null
	*/

	mono_mb_emit_byte (mb, CEE_LDARG_0);
	*obj_null_branch = mono_mb_emit_short_branch (mb, CEE_BRFALSE_S);

	/*
	 	ldarg				0															obj
		conv.i																		objp
		ldc.i4			G_STRUCT_OFFSET(MonoObject, synchronisation)	objp off
		add																			&syncp
		stloc				&syncp
		ldloc				&syncp													&syncp
		ldind.i																		syncp
		stloc				syncp
	 */

	mono_mb_emit_byte (mb, CEE_LDARG_0);
	mono_mb_emit_byte (mb, CEE_CONV_I);
	mono_mb_emit_icon (mb, G_STRUCT_OFFSET (MonoObject, synchronisation));
	mono_mb_emit_byte (mb, CEE_ADD);
	mono_mb_emit_stloc (mb, syncp_add_loc);
	mono_mb_emit_ldloc (mb, syncp_add_loc);
	mono_mb_emit_byte (mb, CEE_LDIND_I);
	mono_mb_emit_stloc (mb, syncp_loc);

	/*
	 	mono.tls			thread_tls_offset										threadp
		ldc.i4			G_STRUCT_OFFSET(MonoInternalThread, thread_info) threadp off
		add																			&thread_info
		ldind.i																		thread_info
		dup																			thread_info thread_info
		brfalse.s		thread_info_null										thread_info
		ldc.i4			G_STRUCT_OFFSET(MonoThreadInfo, small_id)		thread_info off
		add																			&tid
		ldind.i4																		tid
		stloc				tid
	 */

	mono_mb_emit_byte (mb, MONO_CUSTOM_PREFIX);
	mono_mb_emit_byte (mb, CEE_MONO_TLS);
	mono_mb_emit_i4 (mb, thread_tls_offset);
	mono_mb_emit_icon (mb, G_STRUCT_OFFSET (MonoInternalThread, thread_info));
	mono_mb_emit_byte (mb, CEE_ADD);
	mono_mb_emit_byte (mb, CEE_LDIND_I);
	mono_mb_emit_byte (mb, CEE_DUP);
	*thread_info_null_branch = mono_mb_emit_short_branch (mb, CEE_BRFALSE_S); 
	mono_mb_emit_icon (mb, G_STRUCT_OFFSET (MonoThreadInfo, small_id));
	mono_mb_emit_byte (mb, CEE_ADD);
	mono_mb_emit_byte (mb, CEE_LDIND_I);
	mono_mb_emit_stloc (mb, tid_loc);
}

static MonoMethod* monitor_il_fastpaths[3];

gboolean
mono_monitor_is_il_fastpath_wrapper (MonoMethod *method)
{
	int i;
	for (i = 0; i < 3; ++i) {
		if (monitor_il_fastpaths [i] == method)
			return TRUE;
	}
	return FALSE;
}

enum {
	FASTPATH_ENTER,
	FASTPATH_ENTERV4,
	FASTPATH_EXIT
};

static MonoMethod*
register_fastpath (MonoMethod *method, int idx)
{
	mono_memory_barrier ();
	monitor_il_fastpaths [idx] = method;
	return method;
}

static MonoMethod*
mono_monitor_get_fast_enter_method (MonoMethod *monitor_enter_method)
{
	MonoMethodBuilder *mb;
	MonoMethod *ret;
	static MonoMethod *compare_exchange_method;
	int true_locktaken_branch, obj_null_branch, thread_info_null_branch, not_free_branch,
		 contention_branch, inflated_branch, other_owner_branch, max_nest_branch;
	int syncp_add_loc, syncp_loc, tid_loc;
	int thread_tls_offset;
	gboolean is_v4 = mono_method_signature (monitor_enter_method)->param_count == 2;
	int fast_path_idx = is_v4 ? FASTPATH_ENTERV4 : FASTPATH_ENTER;

	thread_tls_offset = mono_thread_get_tls_offset ();
	if (thread_tls_offset == -1) {
		return NULL;
	}

	if (monitor_il_fastpaths [fast_path_idx]) {
		return monitor_il_fastpaths [fast_path_idx];
	}

	if (!compare_exchange_method) {
		MonoMethodDesc *desc;
		MonoClass *class;

		desc = mono_method_desc_new ("Interlocked:CompareExchange(intptr&,intptr,intptr)", FALSE);
		class = mono_class_from_name (mono_defaults.corlib, "System.Threading", "Interlocked");
		compare_exchange_method = mono_method_desc_search_in_class (desc, class);
		mono_method_desc_free (desc);

		if (!compare_exchange_method) {
			return NULL;
		}
	}

	mb = mono_mb_new (mono_defaults.monitor_class, is_v4 ? "FastMonitorEnterV4" : "FastMonitorEnter", MONO_WRAPPER_UNKNOWN);

	mb->method->slot = -1;
	mb->method->flags = METHOD_ATTRIBUTE_PUBLIC | METHOD_ATTRIBUTE_STATIC |
							  METHOD_ATTRIBUTE_HIDE_BY_SIG | METHOD_ATTRIBUTE_FINAL;

	syncp_add_loc = mono_mb_add_local (mb, &mono_defaults.int_class->byval_arg);
	syncp_loc = mono_mb_add_local (mb, &mono_defaults.int_class->byval_arg);
	tid_loc = mono_mb_add_local (mb, &mono_defaults.int32_class->byval_arg);

	/*
	  	ldarg.1																		&lockTaken
		ldind.i1																		lockTaken
		brtrue.s	 		true_locktaken			
	 */

	if (is_v4) {
		mono_mb_emit_byte (mb, CEE_LDARG_1);
		mono_mb_emit_byte (mb, CEE_LDIND_I1);
		true_locktaken_branch = mono_mb_emit_short_branch (mb, CEE_BRTRUE_S);
	}
	
	emit_obj_syncp_check (mb, thread_tls_offset, syncp_add_loc, syncp_loc, tid_loc, &obj_null_branch, &thread_info_null_branch);

	/*
	 	ldloc				syncp														syncp
		brtrue.s			not_free 
		ldloc				&syncp													&syncp			
		ldloc				tid														&syncp tid
		ldc.i4.s 		LOCK_WORD_OWNER_SHIFT								&syncp tid LOCK_WORD_OWNER_SHIFT
		shl 																			&syncp (tid << LOCK_WORD_OWNER_SHIFT)
		ldc.i4			0															&syncp (tid << LOCK_WORD_OWNER_SHIFT) 0
		call				System.Threading.Interlocked.CompareExchange	owner
		brtrue.s			contention
		ret
	 */

	mono_mb_emit_ldloc (mb, syncp_loc);
	not_free_branch = mono_mb_emit_short_branch (mb, CEE_BRTRUE_S);
	mono_mb_emit_ldloc (mb, syncp_add_loc);
	mono_mb_emit_ldloc (mb, tid_loc);
	mono_mb_emit_icon (mb, LOCK_WORD_OWNER_SHIFT);
	mono_mb_emit_byte (mb, CEE_SHL);
	mono_mb_emit_byte (mb, CEE_LDC_I4_0);
	mono_mb_emit_managed_call (mb, compare_exchange_method, NULL);
	contention_branch = mono_mb_emit_short_branch (mb, CEE_BRTRUE_S);

	if (is_v4) {
		mono_mb_emit_byte (mb, CEE_LDARG_1);
		mono_mb_emit_byte (mb, CEE_LDC_I4_1);
		mono_mb_emit_byte (mb, CEE_STIND_I1);
	}
	mono_mb_emit_byte (mb, CEE_RET);

	/*
	not_free:
		ldloc				syncp														syncp													
		ldc.i4.s			LOCK_WORD_BITS_MASK									syncp LOCK_WORD_BITS_MASK
		and																			(syncp & LOCK_WORD_BITS_MASK)
		brtrue.s			inflated
		ldloc				syncp														syncp													
		ldc.i4.s			LOCK_WORD_OWNER_SHIFT								syncp LOCK_WORD_OWNER_SHIFT
		shr.un																		(syncp >> LOCK_WORD_OWNER_SHIFT)
		ldloc				tid														(syncp >> LOCK_WORD_OWNER_SHIFT) tid
		bne.un.s			other_owner																		
		ldc.i4.s 		LOCK_WORD_NEST_MASK									LOCK_WORD_NEST_MASK
		dup																			LOCK_WORD_NEST_MASK LOCK_WORD_NEST_MASK
		ldloc				syncp														LOCK_WORD_NEST_MASK LOCK_WORD_NEST_MASK syncp		
		and																			LOCK_WORD_NEST_MASK (syncp & LOCK_WORD_NEST_MASK)
		beq.s 			max_nest																			
		ldloc				&syncp													&syncp																			
		ldloc				syncp														&syncp syncp
		ldc.i4 			1 << LOCK_WORD_NEST_SHIFT                    &syncp syncp (1 << LOCK_WORD_NEST_SHIFT)
		add 																			&syncp (syncp + (1 << LOCK_WORD_NEST_SHIFT))
		stind.i
		ret
	 */

	mono_mb_patch_short_branch (mb, not_free_branch);
	mono_mb_emit_ldloc (mb, syncp_loc);
	mono_mb_emit_icon (mb, LOCK_WORD_BITS_MASK);
	mono_mb_emit_byte (mb, CEE_AND);
	inflated_branch = mono_mb_emit_short_branch (mb, CEE_BRTRUE_S);
	mono_mb_emit_ldloc (mb, syncp_loc);
	mono_mb_emit_icon (mb, LOCK_WORD_OWNER_SHIFT);
	mono_mb_emit_byte (mb, CEE_SHR_UN);
	mono_mb_emit_ldloc (mb, tid_loc);
	other_owner_branch = mono_mb_emit_short_branch (mb, CEE_BNE_UN_S);
	mono_mb_emit_icon (mb, LOCK_WORD_NEST_MASK);
	mono_mb_emit_byte (mb, CEE_DUP);
	mono_mb_emit_ldloc (mb, syncp_loc);
	mono_mb_emit_byte (mb, CEE_AND);
	max_nest_branch = mono_mb_emit_short_branch (mb, CEE_BEQ_S);
	mono_mb_emit_ldloc (mb, syncp_add_loc);
	mono_mb_emit_ldloc (mb, syncp_loc);
	mono_mb_emit_icon (mb, 1 << LOCK_WORD_NEST_SHIFT);
	mono_mb_emit_byte (mb, CEE_ADD);
	mono_mb_emit_byte (mb, CEE_STIND_I);
	
	if (is_v4) {
		mono_mb_emit_byte (mb, CEE_LDARG_1);
		mono_mb_emit_byte (mb, CEE_LDC_I4_1);
		mono_mb_emit_byte (mb, CEE_STIND_I1);
	}

	mono_mb_emit_byte (mb, CEE_RET);

	/*
	thread_info_null:
	  pop
	true_locktaken, obj_null,  contention, inflated_branch, other_owner, max_nest:
	  ldarg				0															obj
	  call				System.Threading.Monitor.Enter
	  ret
	*/

	mono_mb_patch_short_branch (mb, thread_info_null_branch);
	mono_mb_emit_byte (mb, CEE_POP);

	if (is_v4) {
		mono_mb_patch_short_branch (mb, true_locktaken_branch);
	}
	mono_mb_patch_short_branch (mb, obj_null_branch);
	mono_mb_patch_short_branch (mb, contention_branch);
	mono_mb_patch_short_branch (mb, inflated_branch);
	mono_mb_patch_short_branch (mb, other_owner_branch);
	mono_mb_patch_short_branch (mb, max_nest_branch);

	mono_mb_emit_byte (mb, CEE_LDARG_0);
	if (is_v4) {
		mono_mb_emit_byte (mb, CEE_LDARG_1);
	}
	mono_mb_emit_managed_call (mb, monitor_enter_method, NULL);
	mono_mb_emit_byte (mb, CEE_RET);

	ret = register_fastpath (mono_mb_create_method (mb, mono_signature_no_pinvoke (monitor_enter_method), 5), fast_path_idx);
	mono_mb_free (mb);
	return ret;
}

static MonoMethod*
mono_monitor_get_fast_exit_method (MonoMethod *monitor_exit_method)
{
	MonoMethodBuilder *mb;
	MonoMethod *ret;
	int obj_null_branch, thread_info_null_branch, inflated_branch, other_owner_branch, nested_branch, success_branch;
	int thread_tls_offset;
	int syncp_add_loc, syncp_loc, tid_loc;

	thread_tls_offset = mono_thread_get_tls_offset ();
	if (thread_tls_offset == -1) {
		return NULL;
	}

	if (monitor_il_fastpaths [FASTPATH_EXIT]) {
		return monitor_il_fastpaths [FASTPATH_EXIT];
	}

	mb = mono_mb_new (mono_defaults.monitor_class, "FastMonitorExit", MONO_WRAPPER_UNKNOWN);

	mb->method->slot = -1;
	mb->method->flags = METHOD_ATTRIBUTE_PUBLIC | METHOD_ATTRIBUTE_STATIC |
							  METHOD_ATTRIBUTE_HIDE_BY_SIG | METHOD_ATTRIBUTE_FINAL;

	syncp_add_loc = mono_mb_add_local (mb, &mono_defaults.int_class->byval_arg);
	syncp_loc = mono_mb_add_local (mb, &mono_defaults.int_class->byval_arg);
	tid_loc = mono_mb_add_local (mb, &mono_defaults.int32_class->byval_arg);
	
	emit_obj_syncp_check (mb, thread_tls_offset, syncp_add_loc, syncp_loc, tid_loc, &obj_null_branch, &thread_info_null_branch);

	/*
		ldloc				syncp														syncp													
		ldc.i4.s			LOCK_WORD_BITS_MASK									syncp LOCK_WORD_BITS_MASK
		and																			(syncp & LOCK_WORD_BITS_MASK)
		brtrue.s			inflated
		ldloc				syncp														syncp													
		ldc.i4.s			LOCK_WORD_OWNER_SHIFT								syncp LOCK_WORD_OWNER_SHIFT
		shr.un																		(syncp >> LOCK_WORD_OWNER_SHIFT)
		ldloc				tid														(syncp >> LOCK_WORD_OWNER_SHIFT) tid
		bne.un.s			other_owner	
		ldloc				&syncp													&syncp	
		ldloc				syncp														&syncp syncp		
		ldc.i4.s 		LOCK_WORD_NEST_MASK									&syncp syncp LOCK_WORD_NEST_MASK
		and																			&syncp (syncp & LOCK_WORD_NEST_MASK)
		brtrue.s 		nested													&syncp				
		ldc.i4			0															&syncp 0
		br.s				success													&syncp 0
	nested_branch:
		ldloc				syncp														&syncp syncp
		ldc.i4 			1 << LOCK_WORD_NEST_SHIFT                    &syncp syncp (1 << LOCK_WORD_NEST_SHIFT)
		sub 																			&syncp (syncp - (1 << LOCK_WORD_NEST_SHIFT))
	success_branch:
		volatile.stind.i													&syncp [0 | (syncp - (1 << LOCK_WORD_NEST_SHIFT))]
		ret
	 */

	mono_mb_emit_ldloc (mb, syncp_loc);
	mono_mb_emit_icon (mb, LOCK_WORD_BITS_MASK);
	mono_mb_emit_byte (mb, CEE_AND);
	inflated_branch = mono_mb_emit_short_branch (mb, CEE_BRTRUE_S);
	mono_mb_emit_ldloc (mb, syncp_loc);
	mono_mb_emit_icon (mb, LOCK_WORD_OWNER_SHIFT);
	mono_mb_emit_byte (mb, CEE_SHR_UN);
	mono_mb_emit_ldloc (mb, tid_loc);
	other_owner_branch = mono_mb_emit_short_branch (mb, CEE_BNE_UN_S);
	mono_mb_emit_ldloc (mb, syncp_add_loc);
	mono_mb_emit_ldloc (mb, syncp_loc);
	mono_mb_emit_icon (mb, LOCK_WORD_NEST_MASK);
	mono_mb_emit_byte (mb, CEE_AND);
	nested_branch = mono_mb_emit_short_branch (mb, CEE_BRTRUE_S);
	mono_mb_emit_byte (mb, CEE_LDNULL);
	success_branch = mono_mb_emit_short_branch (mb, CEE_BR_S);
	mono_mb_patch_short_branch (mb, nested_branch);
	mono_mb_emit_ldloc (mb, syncp_loc);
	mono_mb_emit_icon (mb, 1 << LOCK_WORD_NEST_SHIFT);
	mono_mb_emit_byte (mb, CEE_SUB);
	mono_mb_patch_short_branch (mb, success_branch);
	mono_mb_emit_byte (mb, CEE_VOLATILE_);
	mono_mb_emit_byte (mb, CEE_STIND_I);
	mono_mb_emit_byte (mb, CEE_RET);
	
	/*
	thread_info_null:
	  pop
	obj_null_branch, inflated_branch, other_owner_branch:
	  ldarg				0															obj
	  call				System.Threading.Monitor.Exit
	  ret
	 */

	mono_mb_patch_short_branch (mb, thread_info_null_branch);
	mono_mb_emit_byte (mb, CEE_POP);

	mono_mb_patch_short_branch (mb, obj_null_branch);
	mono_mb_patch_short_branch (mb, inflated_branch);
	mono_mb_patch_short_branch (mb, other_owner_branch);

	mono_mb_emit_byte (mb, CEE_LDARG_0);
	mono_mb_emit_managed_call (mb, monitor_exit_method, NULL);
	mono_mb_emit_byte (mb, CEE_RET);

	ret = register_fastpath (mono_mb_create_method (mb, mono_signature_no_pinvoke (monitor_exit_method), 5), FASTPATH_EXIT);
	mono_mb_free (mb);

	return ret;
}

MonoMethod*
mono_monitor_get_fast_path (MonoMethod *enter_or_exit)
{
	if (strcmp (enter_or_exit->name, "Enter") == 0)
		return mono_monitor_get_fast_enter_method (enter_or_exit);
	if (strcmp (enter_or_exit->name, "Exit") == 0)
		return mono_monitor_get_fast_exit_method (enter_or_exit);
	g_assert_not_reached ();
	return NULL;
}

gboolean 
ves_icall_System_Threading_Monitor_Monitor_try_enter (MonoObject *obj, guint32 ms)
{
	gint32 ret;

	do {
		ret = mono_monitor_try_enter_internal (obj, ms, TRUE);
		if (ret == -1)
			mono_thread_interruption_checkpoint ();
	} while (ret == -1);
	
	return ret == 1;
}

void
ves_icall_System_Threading_Monitor_Monitor_try_enter_with_atomic_var (MonoObject *obj, guint32 ms, char *lockTaken)
{
	gint32 ret;
	do {
		ret = mono_monitor_try_enter_internal (obj, ms, TRUE);
		/*This means we got interrupted during the wait and didn't got the monitor.*/
		if (ret == -1)
			mono_thread_interruption_checkpoint ();
	} while (ret == -1);
	/*It's safe to do it from here since interruption would happen only on the wrapper.*/
	*lockTaken = ret == 1;
}

/* 
 * The pulse and wait functions are called with the lock held by the current thread.
 */

void
ves_icall_System_Threading_Monitor_Monitor_pulse (MonoObject *obj)
{
	LockWord lw;
	ListEntry *head, *entry;
   WaitBlock *wait_block;
	
	LOCK_DEBUG (g_message ("%s: (%d) Pulsing %p", __func__, mono_thread_info_get_small_id (), obj));
	
	lw.sync = obj->synchronisation;

	mono_monitor_ensure_synchronized (lw);

	if ((lw.lock_word & LOCK_WORD_INFLATED) == 0) {
		
		/*
		 * We assume that we're racing with a waiter, so we preemptively
		 * inflate the monitor.
		 */

		mono_monitor_inflate_owned (obj);
		lw.sync = obj->synchronisation;
	}

	lw.lock_word &= ~LOCK_WORD_BITS_MASK;

	head = &lw.sync->wait_list;
   if ((entry = head->flink) != head) {
		do {	
			st_list_remove_entry (entry);
			wait_block = CONTAINING_RECORD (entry, WaitBlock, wait_list_entry);
        
			if (st_parker_try_lock (wait_block->parker)) {
				st_reentrant_fair_lock_enqueue_locked (&lw.sync->lock, wait_block);
				LOCK_DEBUG (g_message ("%s: (%d) Moving a thread from the condition the lock's queue", __func__, mono_thread_info_get_small_id ()));
				break;
			}
			
			entry->flink = entry;			
		} while ((entry = head->flink) != head);
	}
}

void
ves_icall_System_Threading_Monitor_Monitor_pulse_all (MonoObject *obj)
{
	LockWord lw;
	ListEntry *head, *entry, *next;
   WaitBlock *wait_block;

	LOCK_DEBUG (g_message("%s: (%d) Pulsing all %p", __func__, mono_thread_info_get_small_id (), obj));

	lw.sync = obj->synchronisation;

	mono_monitor_ensure_synchronized (lw);

	if ((lw.lock_word & LOCK_WORD_INFLATED) == 0) {
		
		/*
		 * We assume that we're racing with a waiter, so we preemptively
		 * inflate the monitor.
		 */

		mono_monitor_inflate_owned (obj);
		lw.sync = obj->synchronisation;
	}
	
	lw.lock_word &= ~LOCK_WORD_BITS_MASK;

	head = &lw.sync->wait_list;
   if ((entry = head->flink) != head) {
		do {	
			next = entry->flink;
			wait_block = CONTAINING_RECORD (entry, WaitBlock, wait_list_entry);
        
			if (st_parker_try_lock (wait_block->parker)) {
				st_reentrant_fair_lock_enqueue_locked (&lw.sync->lock, wait_block);
				LOCK_DEBUG (g_message ("%s: (%d) Moving a thread from the condition the lock's queue", __func__, mono_thread_info_get_small_id ()));
			} else {			
				entry->flink = entry;			
			}
		} while ((entry = next) != head);

		st_list_initialize (head);
	}
}

gboolean
ves_icall_System_Threading_Monitor_Monitor_wait (MonoObject *obj, guint32 ms)
{
	LockWord lw;
	gint32 res;
	StParker parker; 
   WaitBlock wait_block;
   guint32 lock_state;
   guint32 wait_status;
	SpinWait spinner;

	LOCK_DEBUG (g_message ("%s: (%d) Trying to wait for %p with timeout %dms", __func__, mono_thread_info_get_small_id (), obj, ms));
	
	lw.sync = obj->synchronisation;

	mono_monitor_ensure_synchronized (lw);

	if ((lw.lock_word & LOCK_WORD_INFLATED) == 0) {
		mono_monitor_inflate_owned (obj);
		lw.sync = obj->synchronisation;
	}

	lw.lock_word &= ~LOCK_WORD_BITS_MASK;

	st_parker_init (&parker, 1);
	st_wait_block_init (&wait_block, &parker, 0, WAIT_SUCCESS);

	st_list_insert_tail (&lw.sync->wait_list, &wait_block.wait_list_entry);
	lock_state = st_reentrant_fair_lock_exit_completly (&lw.sync->lock);	
	wait_status = st_parker_park_ex (&parker, MONITOR_SPIN_COUNT, ms, NULL, TRUE);

	/*
	 * If the wait failed, we must acquire the lock. Even if we're shutting down, we
	 * must ensure that we remove our wait block to avoid invalid accesses. Fix this.
	 */

	if (wait_status != WAIT_SUCCESS) {
		LOCK_DEBUG (g_message ("%s: (%d) Wait failed, reacquiring the lock", __func__, mono_thread_info_get_small_id ()));
		res = wait_status == WAIT_TIMEOUT ? 0 : -1;
      do {
			if (st_reentrant_fair_lock_enter (&lw.sync->lock)) {
            break;
         }
			st_spin_once (&spinner);
      } while (TRUE);
   } else {
		LOCK_DEBUG (g_message ("%s: (%d) Success", __func__, mono_thread_info_get_small_id ()));

		res = 1;
      lw.sync->lock.owner = mono_thread_info_get_small_id ();
   }

	lw.sync->lock.nest = lock_state;
	
	return res;
}
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                   