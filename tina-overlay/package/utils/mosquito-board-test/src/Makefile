all: mosquito-board-test mosquito-location mosquito-lbs

mosquito-board-test: mosquito-board-test.c
	$(CC) $(CFLAGS) -o $@ $< $(LDFLAGS)

mosquito-location: mosquito-location.c
	$(CC) $(CFLAGS) -o $@ $< $(LDFLAGS)

mosquito-lbs: mosquito-lbs.c
	$(CC) $(CFLAGS) -o $@ $< $(LDFLAGS) -lm

clean:
	rm -f mosquito-board-test mosquito-location mosquito-lbs
