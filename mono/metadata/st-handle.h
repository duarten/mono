/*
 * st-handle.h: API for registering waitable instances with the runtime so
 *					 they can be shared among app domains. Used for implementing
 *					 the Handle and SafeWaitHandle properties on WaitHandle.
 *
 * Author:
 *	Duarte Nunes (duarte.m.nunes@gmail.com)
 */

#ifndef _MONO_METADATA_ST_HANDLE_H_
#define _MONO_METADATA_ST_HANDLE_H_

#include <glib.h>
#include <mono/utils/mono-compiler.h>

gpointer ves_icall_System_Threading_StInternalMethods_RegisterHandle_internal (MonoObject *obj) MONO_INTERNAL;
MonoObject * ves_icall_System_Threading_StInternalMethods_ResolveHandle_internal (gpointer handle) MONO_INTERNAL;
MonoBoolean ves_icall_System_Threading_StInternalMethods_RemoveHandle_internal (gpointer handle) MONO_INTERNAL;

#endif // _MONO_METADATA_ST_HANDLE_H_
