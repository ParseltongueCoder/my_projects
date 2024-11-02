users_info = {}
def add_user_info(details_list):
    name, surname = details_list
    profession = input(f"Enter profession for {name} {surname}: ")
    age = input(f"Enter age for {name} {surname}: ")
    city = input(f"Enter city for {name} {surname}: ")
    users_info[f"{name} {surname}"] = {
        "name": name,
        "surname": surname,
        "profession": profession,
        "age": age,
        "city": city
    }

name_surname = ["John", "Doe"]
add_user_info(name_surname)
print(users_info)