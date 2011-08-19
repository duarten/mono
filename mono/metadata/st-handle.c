/*
 * st-handle.c: API for registering waitable instances with the runtime so
 *					 they can be shared among app domains. Used for implementing
 *					 the Handle and SafeWaitHandle properties on WaitHandle.
 *
 * Author:
 *	Duarte Nunes (duarte.m.nunes@gmail.com)
 */
 
#include <mono/metadata/object-internals.h>
#include <mono/metadata/gc-internal.h>
#include <mono/metadata/mono-hash.h>
#include <mono/metadata/st-handle.h>
#include <mono/utils/st.h>

#define SPIN_COUNT 100

/*
 * Table that maps artificial handles to instances of StWaitable.
 * The handles are generated incrementally.
 */

static MonoGHashTable *handles = NULL;
static guint32 cur_handle = 0;
static StLock lock;

/*
 * This API assumes that the same object is not registered more than once,
 * so there may need to be additional upstream synchronization.
 */
 
gpointer 
ves_icall_System_Threading_StInternalMethods_RegisterHandle_internal (MonoObject *obj) 
{
	guint32 handle;

	if (handles == NULL) {
		MONO_GC_REGISTER_ROOT_FIXED (handles);
		handles = mono_g_hash_table_new_type (NULL, NULL, MONO_HASH_VALUE_GC);
		st_lock_init (&lock, SPIN_COUNT);
	}

	st_lock_enter (&lock);
	
	if (handle == (~0 - 1)) {
		handle = cur_handle = 1;
	} else {
		handle += 1;
	}
	
	mono_g_hash_table_insert (handles, (gpointer)handle, (gpointer)obj);
	st_lock_exit (&lock);
	
	return (gpointer) handle;
}

MonoObject * 
ves_icall_System_Threading_StInternalMethods_ResolveHandle_internal (gpointer handle) 
{
	gpointer waitable;

	g_assert (handles != NULL);

	st_lock_enter (&lock);
	waitable = mono_g_hash_table_lookup (handles, handle);
	st_lock_exit (&lock);
	
	return (MonoObject *) waitable;
}

gboolean 
ves_icall_System_Threading_StInternalMethods_RemoveHandle_internal (gpointer handle) 
{
	gboolean removed;
	
	g_assert (handles != NULL);

	st_lock_enter (&lock);
	removed = mono_g_hash_table_remove (handles, handle);
	st_lock_exit (&lock);
	
	return removed;
}
