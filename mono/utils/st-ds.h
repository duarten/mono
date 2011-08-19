/*
 * st-list.h: Data structures used by SlimThreading.
 *
 * Author:
 *	Duarte Nunes (duarte.m.nunes@gmail.com)
 */

#ifndef _MONO_ST_DS_H_
#define _MONO_ST_DS_H_

#include <glib.h>

/*
 * Doubly linked list definitions.
 */

#ifndef CONTAINING_RECORD
#define CONTAINING_RECORD(address, type, field) ((type *)( \
                          (PCHAR)(address) - (ULONG_PTR)(&((type *)0)->field)))
#endif

typedef struct _ListEntry {
    struct _ListEntry * volatile flink;
    struct _ListEntry *blink;
} ListEntry;

static inline void 
st_list_initialize (ListEntry *head)
{
	head->flink = head->blink = head;
}

static inline gboolean
st_list_is_empty (const ListEntry *head)
{
	return head->flink == head;
}

static inline gboolean
st_list_remove_entry (ListEntry *entry) 
{
	ListEntry *blink = entry->blink;
	ListEntry *flink = entry->flink;

	blink->flink = flink;
	flink->blink = blink;
	return flink == blink;
}

static inline ListEntry *
st_list_remove_first (ListEntry *head) 
{
	ListEntry *entry = head->flink;
	ListEntry *flink = entry->flink;

	head->flink = flink;
	flink->blink = head;
	return entry;
}

static inline ListEntry *
st_list_remove_last (ListEntry *head)
{
	ListEntry *entry = head->blink;
	ListEntry *blink = entry->blink;

	head->blink = blink;
	blink->flink = head;	
	return entry;
}

static inline void
st_list_insert_tail (ListEntry *head, ListEntry *entry)
{
	ListEntry *blink = head->blink;

	entry->flink = head;
	head->blink = entry;
	entry->blink = blink;
	blink->flink = entry;
}

static inline void
st_list_insert_head (ListEntry *head, ListEntry *entry)
{
	ListEntry *flink = head->flink;

	entry->flink = flink;
	flink->blink = entry;
	entry->blink = head;
	head->flink = entry;
}

#endif // _MONO_ST_DS_H_
