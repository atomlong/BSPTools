/* This file is not yet automatically regenerated by VisualGDB and can be edited manually. */

#pragma once
#include "bsp_api.h"
#include "FreeRTOS.h"
#include "task.h"
#include "semphr.h"
#include "hal_data.h"
#ifdef __cplusplus
extern "C" void blinky_thread_entry(void * pvParameters);
#else
extern void blinky_thread_entry(void *pvParameters);
#endif
FSP_HEADER
FSP_FOOTER

