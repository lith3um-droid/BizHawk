# Native harness: PicoDrive's sources, bizhawk.c and harness.c, built with the
# host compiler and the core's own defines (see ../Makefile).
#
#   make                          this tree's core, clang, in build/clang
#   make CC=gcc OUT=build/gcc     FAME/C in its computed goto mode
#   make CORE=/other/waterbox/picodrive OUT=build/pristine
#                                 another tree's core, e.g. an unmodified one
#   make LTO=1 ...                link time optimization, as the waterbox does
#
# clang builds FAME/C with FAMEC_NO_GOTOS, like the shipped core.

HERE := $(patsubst %/,%,$(dir $(realpath $(lastword $(MAKEFILE_LIST)))))
CC := clang
CORE ?= $(realpath $(HERE)/..)
OUT ?= $(HERE)/build/$(CC)

SRCS := $(shell find $(CORE) -type f -name '*.c' -not -path '*/hooktest/*')
OBJS := $(patsubst $(CORE)/%.c,$(OUT)/obj/%.o,$(SRCS)) $(OUT)/obj/harness.o

CFLAGS := -I$(CORE) -I$(CORE)/../emulibc -std=c99 -O3 \
	-fomit-frame-pointer -falign-functions=16 -mcmodel=large -fno-pic -fno-pie \
	-Werror=implicit-function-declaration -MMD -MP \
	-DLSB_FIRST -DNDEBUG -DEMU_F68K -D_USE_CZ80
LDFLAGS := -no-pie
ifeq ($(LTO),1)
CFLAGS += -flto
LDFLAGS += -flto -O3
ifeq ($(CC),clang)
LDFLAGS += -fuse-ld=lld
endif
endif

$(OUT)/harness: $(OBJS)
	@echo ld $@
	@$(CC) $(LDFLAGS) -o $@ $^ -lm

# the core's warnings are not ours to fix; bizhawk.c's main() is a waterbox stub
$(OUT)/obj/%.o: $(CORE)/%.c
	@mkdir -p $(@D)
	@echo cc $<
	@$(CC) -c -o $@ $< $(CFLAGS) -w $(if $(filter %/bizhawk.c,$<),-Dmain=bizhawk_main)

$(OUT)/obj/harness.o: $(HERE)/harness.c
	@mkdir -p $(@D)
	@echo cc $<
	@$(CC) -c -o $@ $< $(CFLAGS) -Wall

clean:
	rm -rf $(HERE)/build

-include $(OBJS:.o=.d)

.PHONY: clean
