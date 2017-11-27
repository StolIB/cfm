all:

clean:
	-rm -f build/*

syn: build/icestick-256.asc

program: build/icestick-256-prog.bin
	iceprog $<

build:
	mkdir build

build/test.hex: build tools/test.fs
	cd tools && \
	  stack --silent setup && \
	  stack --silent build && \
	  stack --silent exec tools-exe test.fs ../build/test.hex

build/icestick-256.asc: build
	cd rtl && \
	  stack --silent setup && \
	  stack --silent build --only-dependencies
	rtl/syn/syn-1k.sh
	cp rtl/syn/out/syn1k.asc build/icestick-256.asc

build/icestick-256-prog.asc: build/icestick-256.asc build/test.hex
	icebram -v rtl/syn/random-256.hex build/test.hex \
	  < build/icestick-256.asc \
	  > build/icestick-256-prog.asc

%.bin: %.asc
	icepack < $< > $@

FORCE:

.PHONY: all clean syn
