def process(values: list[int]) -> int:
    total = 0
    for value in values:
        total += value
    if total > 10:
        total -= 1
    total += 5
    return total
